using System.Text.Json;
using AgentSignal.Core;
using Microsoft.Data.Sqlite;

namespace AgentSignal.Writer;

/// <summary>
/// The Antigravity red light. No hook fires at (or during, or at the resolution of) an approval
/// prompt, so red comes from polling the IDE's per-conversation SQLite instead — Phase 0 decoded
/// <c>steps.status</c> and proved the db reflects the prompt within ~0.3s (FINDINGS §2–3):
///
///   9 = awaiting approval · 2 = tool executing (post-approval) · 8 = model streaming
///   3 = completed · 6 = cancelled · 7 = denied at the prompt
///
/// The poller owns ONLY the approval lifecycle; the hooks own the turn edges (PreInvocation→yellow,
/// Stop→green — and Stop fires on cancel too, so there is no stuck-yellow here):
///
///   session not red + a NEW status-9 step  → red    (prompt is showing)
///   session red + no status-9 left         → yellow if a step is running/streaming (2/8), else
///                                            green — so red ends AT the approval instant (9→2),
///                                            and a deny (9→7) clears immediately
///
/// It deliberately never writes green from "all steps completed": between two model invocations of
/// one turn every step is momentarily 3, and writing green there would flash a false "done" mid-turn
/// (the same false-green failure that got the Claude stale-yellow demotion reversed — Decision #3).
/// Green stays the Stop hook's job. A status-9 step seen while the session already sits green after
/// Stop, with the db not written since (a cancel at the prompt leaves one behind), is remembered and
/// ignored so an idle pill is never re-reddened by a dead prompt.
///
/// Reading the LIVE db is never safe (WAL, written 4+ times/sec while streaming), so each changed db
/// is copied — db + -wal + -shm — and the copy is opened (Phase 0 ran this exact scheme at 4 reads/s
/// for two 30-minute sessions with zero errors). The file is stat'ed first and only re-read when
/// mtime/size moved. Sessions whose IDE process died are deleted here too (belt-and-braces with the
/// widget's own liveness filter), so ended Antigravity sessions leave no ghost pills.
/// </summary>
internal static class AntigravityPoller
{
    private const int TickMs = 250;
    private const int IdleExitSeconds = 60;

    public static int RunCommand(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], AntigravityAdapter.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: AgentSignal.Writer poll antigravity [--once|--test]");
            return 1;
        }
        if (args.Contains("--test")) return SelfTest.Run();

        var poller = new Poller(AgentPaths.SessionsDir, AntigravityAdapter.ConversationsDir,
                                Path.Combine(AgentPaths.Root, "tmp", "antigravity"));
        if (args.Contains("--once"))
        {
            poller.Tick(print: true);
            return 0;
        }
        return Daemon(poller);
    }

    private static int Daemon(Poller poller)
    {
        AgentPaths.EnsureRoot();
        FileStream? theLock = null;
        try
        {
            try
            {
                theLock = new FileStream(AntigravityAdapter.PollerLockFile,
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                theLock.SetLength(0);
                byte[] pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
                theLock.Write(pid); theLock.Flush();
            }
            catch (IOException)
            {
                return 0; // another instance is running — exactly what we want
            }

            Log("poller started");
            DateTime idleSince = DateTime.UtcNow;
            while (true)
            {
                int live;
                try { live = poller.Tick(print: false); }
                catch (Exception ex) { Log($"tick error: {ex.Message}"); live = 1; /* stay alive */ }

                if (live > 0) idleSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - idleSince).TotalSeconds > IdleExitSeconds)
                    break; // no antigravity sessions for a while — the next hook respawns us
                Thread.Sleep(TickMs);
            }
            Log("poller idle-exit");
            return 0;
        }
        finally
        {
            theLock?.Dispose();
            try { File.Delete(AntigravityAdapter.PollerLockFile); } catch { }
            poller.CleanupTemp();
        }
    }

    internal static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("AGENTSIGNAL_DEBUG") != "1") return;
        try
        {
            AgentPaths.EnsureRoot();
            File.AppendAllText(Path.Combine(AgentPaths.Root, "antigravity-poller.log"),
                $"{DateTimeOffset.UtcNow:o} {message}\n");
        }
        catch { /* debug only */ }
    }

    // ==================================================================================== Poller ==

    internal sealed class Poller(string sessionsDir, string conversationsDir, string tempDir,
                                 int quietGreenMs = Poller.DefaultQuietGreenMs)
    {
        /// <summary>How long the db must sit fully quiescent (no running/streaming/pending step)
        /// before a yellow session is declared green. The engine never actually spawns
        /// invocation-level hooks (Stop/PreInvocation — verified live 2026-07-06), so turn-end
        /// green comes from this debounce. Phase-0 timeline data shows mid-turn all-final gaps
        /// (model spin-up after a tool) of at most ~1.9s, so 5s clears the worst observed case
        /// ~2.7×; a pathological longer gap would flash green and self-correct to yellow.</summary>
        public const int DefaultQuietGreenMs = 5000;

        private const string FilePrefix = "antigravity__";
        private readonly Dictionary<string, Conv> _convs = new();

        private sealed class Conv
        {
            public (long DbTicks, long DbSize, long WalTicks, long WalSize) Stamp;
            public HashSet<long> Nines = new();      // idx of steps currently status 9
            public bool Active;                       // any step status 2 or 8
            public readonly HashSet<long> Ignored = new(); // stale 9s orphaned by a cancel-at-prompt
            public long LastDbChangeUnix;             // newest mtime among db/-wal at last read
            public long QuietSinceMs;                 // first tick with no active/pending step; 0 = not quiet
        }

        /// <summary>One poll pass over every antigravity session file. Returns how many live session
        /// files remain (drives the daemon's idle-exit).</summary>
        public int Tick(bool print)
        {
            string[] files;
            try { files = Directory.GetFiles(sessionsDir, FilePrefix + "*.json"); }
            catch { return 0; }

            // One process enumeration per tick, shared by every pid-less session (computed lazily, so
            // it normally never runs).
            List<DateTime>? ideStarts = null;
            int live = 0;
            foreach (string file in files)
            {
                SessionState? s = TryRead(file);
                if (s is null) { live++; continue; } // mid-write; next tick

                // Ghost cleanup: the session's IDE process is gone (Antigravity has no session-end
                // hook, so this — plus the widget's sweep — is how ended sessions vanish). Uses the
                // SAME rule as the widget (SessionLiveness: pid + start time), not a bare "is some
                // process on this pid" probe — pids are recycled, and because this poller rewrites
                // ts=now on the files it keeps, trusting a recycled pid here would refresh a ghost's
                // ts and make it look alive to the widget too.
                SessionLiveness.Verdict verdict = SessionLiveness.Check(s, tool =>
                    ideStarts ??= ProcessHelper.StartTimesOfProcessesNamed(SessionLiveness.AgentProcessNames(tool)));
                if (!verdict.Alive)
                {
                    try { File.Delete(file); } catch { }
                    string convId = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                    _convs.Remove(convId);
                    Log($"{convId}: IDE gone — session file removed ({verdict.Reason})");
                    if (print) Console.WriteLine($"{convId}: IDE process gone -> session removed");
                    continue;
                }
                live++;

                string id = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                string db = Path.Combine(conversationsDir, id + ".db");
                if (!File.Exists(db))
                {
                    if (print) Console.WriteLine($"{id}: no conversation db — hooks only (state {s.State})");
                    continue; // unknown/foreign id; yellow/green still work via hooks
                }

                if (!_convs.TryGetValue(id, out Conv? conv))
                    _convs[id] = conv = new Conv();

                var stamp = Stat(db);
                if (stamp != conv.Stamp && ReadDb(id, db, conv))
                    conv.Stamp = stamp;

                Arbitrate(file, s, conv, print, id);
            }
            return live;
        }

        private static (long, long, long, long) Stat(string db)
        {
            long dbT = 0, dbS = 0, walT = 0, walS = 0;
            try { var fi = new FileInfo(db); dbT = fi.LastWriteTimeUtc.Ticks; dbS = fi.Length; } catch { }
            try
            {
                var wi = new FileInfo(db + "-wal");
                if (wi.Exists) { walT = wi.LastWriteTimeUtc.Ticks; walS = wi.Length; }
            }
            catch { }
            return (dbT, dbS, walT, walS);
        }

        /// <summary>Copy db (+wal/+shm) and read step statuses from the COPY. Returns false on any
        /// failure (keep the previous snapshot and retry next tick).</summary>
        private bool ReadDb(string id, string db, Conv conv)
        {
            try
            {
                Directory.CreateDirectory(tempDir);
                string copy = Path.Combine(tempDir, id + ".db");
                File.Copy(db, copy, overwrite: true);
                foreach (string ext in new[] { "-wal", "-shm" })
                {
                    string side = db + ext;
                    string sideCopy = copy + ext;
                    if (File.Exists(side)) File.Copy(side, sideCopy, overwrite: true);
                    else if (File.Exists(sideCopy)) File.Delete(sideCopy); // stale sidecar corrupts the copy
                }

                var nines = new HashSet<long>();
                bool active = false;
                using (var conn = new SqliteConnection($"Data Source={copy};Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT \"idx\", \"status\" FROM \"steps\"";
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        long idx = r.IsDBNull(0) ? -1 : r.GetInt64(0);
                        long status = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                        if (status == 9) nines.Add(idx);
                        else if (status is 2 or 8) active = true;
                    }
                }

                conv.Nines = nines;
                conv.Active = active;
                conv.Ignored.RemoveWhere(i => !nines.Contains(i)); // resolved 9s need no memory
                long dbUnix = new DateTimeOffset(File.GetLastWriteTimeUtc(db)).ToUnixTimeSeconds();
                string wal = db + "-wal";
                if (File.Exists(wal))
                    dbUnix = Math.Max(dbUnix, new DateTimeOffset(File.GetLastWriteTimeUtc(wal)).ToUnixTimeSeconds());
                conv.LastDbChangeUnix = dbUnix;
                return true;
            }
            catch (Exception ex)
            {
                Log($"{id}: db read failed ({ex.GetType().Name}: {ex.Message}) — keeping previous snapshot");
                return false;
            }
        }

        private void Arbitrate(string file, SessionState s, Conv conv, bool print, string id)
        {
            int newNines = conv.Nines.Count(n => !conv.Ignored.Contains(n));

            // Track quiescence: the first tick where the db shows neither a pending approval nor a
            // running/streaming step starts the quiet clock; any activity resets it.
            long nowMs = Environment.TickCount64;
            if (conv.Active || newNines > 0) conv.QuietSinceMs = 0;
            else if (conv.QuietSinceMs == 0) conv.QuietSinceMs = nowMs;

            if (s.State == "red")
            {
                if (newNines == 0)
                {
                    // The prompt was answered: approve (9→2, a step now executes) resumes yellow at
                    // this very instant; deny (9→7) clears — yellow if the model is already
                    // responding, green otherwise.
                    string next = conv.Active ? "yellow" : "green";
                    Write(file, s, next, "ApprovalResolved");
                    Log($"{id}: red -> {next} (approval resolved)");
                    if (print) Console.WriteLine($"{id}: red -> {next} (approval resolved)");
                }
                else if (print) Console.WriteLine($"{id}: red (awaiting approval, {newNines} pending)");
                return;
            }

            if (newNines > 0)
            {
                // A 9 already sitting in a db that hasn't been written since the session went green
                // is an orphan from a cancel at the prompt — remember it, never red on it.
                // (A REAL new prompt writes the db AFTER the green, so it always passes this gate.)
                if (s.State == "green" && conv.LastDbChangeUnix <= s.Ts)
                {
                    conv.Ignored.UnionWith(conv.Nines);
                    Log($"{id}: ignoring {conv.Nines.Count} stale 9(s) predating the green");
                    if (print) Console.WriteLine($"{id}: {conv.Nines.Count} stale 9(s) predate the green — ignored, stays {s.State}");
                    return;
                }
                Write(file, s, "red", "ApprovalPending");
                Log($"{id}: {s.State} -> red (approval prompt showing)");
                if (print) Console.WriteLine($"{id}: {s.State} -> red (approval prompt showing)");
                return;
            }

            // Hook-missed activity: the engine only spawns TOOL hooks, so a no-tool stretch (model
            // streaming a chat answer) shows yellow via the db instead. Current-read state, so no
            // staleness gate is needed — if a step is running/streaming, the turn IS running.
            if (conv.Active)
            {
                if (s.State == "green")
                {
                    Write(file, s, "yellow", "DbActive");
                    Log($"{id}: green -> yellow (db shows activity)");
                    if (print) Console.WriteLine($"{id}: green -> yellow (db shows activity)");
                }
                else if (print) Console.WriteLine($"{id}: {s.State} (db active)");
                return;
            }

            // Turn-end green: invocation hooks (Stop) never actually run in this engine build, so a
            // yellow session goes green once the db has been fully quiescent for the debounce
            // window (see DefaultQuietGreenMs — mid-turn model-spin-up gaps are far shorter).
            // Covers normal completion AND cancel (a cancel finalises its steps to 6, then quiesces).
            if (s.State == "yellow" && conv.QuietSinceMs != 0 && nowMs - conv.QuietSinceMs >= quietGreenMs)
            {
                Write(file, s, "green", "DbQuiet");
                Log($"{id}: yellow -> green (db quiescent {quietGreenMs}ms)");
                if (print) Console.WriteLine($"{id}: yellow -> green (db quiescent)");
                return;
            }

            if (print) Console.WriteLine($"{id}: {s.State} (db quiet: active={conv.Active}, no pending approvals)");
        }

        private static void Write(string path, SessionState s, string state, string evt)
        {
            s.State = state;
            s.Event = evt;
            s.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            s.DurationMs = null; // pid/tool/sessionId preserved from the file we read
            Program.WriteAtomic(path, s);
        }

        private static SessionState? TryRead(string path)
        {
            try { return JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.SessionState); }
            catch { return null; }
        }

        public void CleanupTemp()
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ================================================================================== SelfTest ==

    /// <summary>
    /// `AgentSignal.Writer poll antigravity --test`: prove the whole approval lifecycle against a
    /// REAL WAL-mode SQLite db in temp dirs — prompt→red, approve→yellow at that instant, deny→clear,
    /// mid-turn completed-gap does NOT go green, stale cancel 9 never re-reds an idle session, and a
    /// dead IDE pid removes the session file. Exit 0 = ALL PASS.
    /// </summary>
    private static class SelfTest
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "agentsignal-antigravity-test-" + Environment.ProcessId);
            string sessions = Path.Combine(root, "sessions");
            string convs = Path.Combine(root, "conversations");
            string tmp = Path.Combine(root, "tmp");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(convs);

            bool all = true;
            try
            {
                const string id = "testconv";
                string db = Path.Combine(convs, id + ".db");
                string sessionFile = Path.Combine(sessions, "antigravity__" + id + ".json");

                using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
                conn.Open();
                Exec(conn, "PRAGMA journal_mode=WAL");
                Exec(conn, "CREATE TABLE steps (\"idx\" INTEGER, step_type INTEGER, status INTEGER)");
                Exec(conn, "INSERT INTO steps VALUES (1, 0, 3), (2, 0, 8)"); // done + streaming

                const int quietMs = 400; // fast debounce so the test runs in ~2s (live default 5s)
                var poller = new Poller(sessions, convs, tmp, quietMs);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Turn running (PreToolUse wrote yellow), model streaming: stays yellow.
                PlantSession(sessionFile, "yellow", "PreToolUse", Environment.ProcessId, now);
                poller.Tick(print: false);
                all &= Check("streaming turn stays yellow", ReadState(sessionFile) is ("yellow", "PreToolUse"));

                // The approval prompt appears (a status-9 step lands): red.
                Exec(conn, "INSERT INTO steps VALUES (3, 1, 9)");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("status 9 -> red", ReadState(sessionFile) is ("red", "ApprovalPending"));

                // Sitting at the prompt: still red (and no flapping).
                poller.Tick(print: false);
                all &= Check("holds red at the prompt", ReadState(sessionFile) is ("red", "ApprovalPending"));

                // APPROVE: 9→2 — red must end at this very instant, not when the tool finishes.
                Exec(conn, "UPDATE steps SET status = 2 WHERE \"idx\" = 3");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("approve (9->2) -> yellow at the approval instant",
                    ReadState(sessionFile) is ("yellow", "ApprovalResolved"));

                // Mid-turn gap: every step momentarily completed — must NOT green inside the
                // debounce window (that gap is just the model spinning up its next invocation).
                Exec(conn, "UPDATE steps SET status = 3");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("all-completed gap does NOT green within the debounce",
                    ReadState(sessionFile) is ("yellow", "ApprovalResolved"));

                // Activity resumes inside the window: the quiet clock resets, still yellow.
                Exec(conn, "INSERT INTO steps VALUES (4, 0, 8)");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("streaming resumes -> still yellow (quiet clock reset)",
                    ReadState(sessionFile) is ("yellow", "ApprovalResolved"));

                // Turn truly ends: quiescent past the debounce -> green (Stop never runs in this
                // engine build, so the debounced db-quiescence IS the green edge).
                Exec(conn, "UPDATE steps SET status = 3 WHERE \"idx\" = 4");
                Touch(db);
                poller.Tick(print: false); // starts the quiet clock
                Thread.Sleep(quietMs + 200);
                poller.Tick(print: false);
                all &= Check("quiescent past debounce -> green (turn end without Stop)",
                    ReadState(sessionFile) is ("green", "DbQuiet"));

                // DENY path: a NEW prompt (db written after the green) -> red; deny (9→7) clears.
                Exec(conn, "INSERT INTO steps VALUES (5, 1, 9)");
                Bump(db, 10);
                poller.Tick(print: false);
                all &= Check("new prompt after green -> red", ReadState(sessionFile) is ("red", "ApprovalPending"));
                Exec(conn, "UPDATE steps SET status = 7 WHERE \"idx\" = 5");
                Bump(db, 11);
                poller.Tick(print: false);
                all &= Check("deny (9->7) clears red (nothing running -> green)",
                    ReadState(sessionFile) is ("green", "ApprovalResolved"));

                // Cancel-at-prompt ghost: a 9 lingers in a db not written since the session went
                // green. The idle session must NOT be re-reddened by the dead prompt.
                Exec(conn, "INSERT INTO steps VALUES (6, 1, 9)");
                Bump(db, 12);
                PlantSession(sessionFile, "green", "DbQuiet", Environment.ProcessId,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 20); // green recorded after that db write
                poller.Tick(print: false);
                all &= Check("stale 9 predating the green stays green",
                    ReadState(sessionFile) is ("green", "DbQuiet"));

                // ...but a genuinely NEW prompt (db written AFTER the green) still reds.
                Exec(conn, "INSERT INTO steps VALUES (7, 1, 9)");
                Bump(db, 30);
                poller.Tick(print: false);
                all &= Check("NEW 9 written after the green -> red (stale-ignore doesn't overreach)",
                    ReadState(sessionFile) is ("red", "ApprovalPending"));
                Exec(conn, "UPDATE steps SET status = 6 WHERE \"idx\" IN (6, 7)"); // cancel both
                Bump(db, 31);
                poller.Tick(print: false);
                all &= Check("cancelled prompts (9->6) clear red",
                    ReadState(sessionFile) is ("green", "ApprovalResolved"));

                // Hook-missed activity: the engine only spawns TOOL hooks, so a no-tool streaming
                // stretch must turn a green session yellow from the db alone.
                Exec(conn, "INSERT INTO steps VALUES (8, 0, 8)");
                Bump(db, 32);
                poller.Tick(print: false);
                all &= Check("green + streaming step -> yellow (no-tool turn, db-driven)",
                    ReadState(sessionFile) is ("yellow", "DbActive"));
                Exec(conn, "UPDATE steps SET status = 3 WHERE \"idx\" = 8");
                Bump(db, 33);
                poller.Tick(print: false); // starts the quiet clock
                Thread.Sleep(quietMs + 200);
                poller.Tick(print: false);
                all &= Check("no-tool turn quiesces -> green", ReadState(sessionFile) is ("green", "DbQuiet"));

                // Ghost cleanup: the IDE process is gone -> the session file is removed.
                PlantSession(sessionFile, "green", "DbQuiet", FindDeadPid(), now);
                poller.Tick(print: false);
                all &= Check("dead IDE pid -> session file removed", !File.Exists(sessionFile));

                poller.CleanupTemp();
            }
            finally
            {
                try { SqliteConnection.ClearAllPools(); } catch { }
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            Console.WriteLine(all ? "\nALL PASS" : "\nFAILURES ABOVE");
            return all ? 0 : 1;
        }

        private static bool Check(string label, bool ok)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
            return ok;
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>Nudge the db file mtime forward so the poller's stat-before-read sees a change
        /// even when several writes land within the filesystem timestamp granularity.</summary>
        private static void Touch(string db)
        {
            File.SetLastWriteTimeUtc(db, DateTime.UtcNow);
            string wal = db + "-wal";
            if (File.Exists(wal)) File.SetLastWriteTimeUtc(wal, DateTime.UtcNow);
        }

        /// <summary>Set the db mtime N seconds into the future — a deterministic "this write happened
        /// AFTER the session file's ts" for exercising the stale-9 gate in both directions.</summary>
        private static void Bump(string db, int seconds)
        {
            DateTime t = DateTime.UtcNow.AddSeconds(seconds);
            File.SetLastWriteTimeUtc(db, t);
            string wal = db + "-wal";
            if (File.Exists(wal)) File.SetLastWriteTimeUtc(wal, t);
        }

        private static void PlantSession(string path, string state, string evt, int pid, long ts)
        {
            var s = new SessionState
            {
                Tool = AntigravityAdapter.ToolName,
                SessionId = "testconv",
                State = state,
                Event = evt,
                Pid = pid,
                Ts = ts,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(s, AgentJsonContext.Default.SessionState));
        }

        private static (string, string?)? ReadState(string path)
        {
            try
            {
                SessionState? s = JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.SessionState);
                return s is null ? null : (s.State, s.Event);
            }
            catch { return null; }
        }

        private static int FindDeadPid()
        {
            for (int pid = 1_000_000; pid > 4; pid -= 7919)
                if (!ProcessHelper.IsAlive(pid))
                    return pid;
            return 999_999;
        }
    }
}
