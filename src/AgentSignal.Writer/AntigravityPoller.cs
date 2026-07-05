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

    internal sealed class Poller(string sessionsDir, string conversationsDir, string tempDir)
    {
        private const string FilePrefix = "antigravity__";
        private readonly Dictionary<string, Conv> _convs = new();

        private sealed class Conv
        {
            public (long DbTicks, long DbSize, long WalTicks, long WalSize) Stamp;
            public HashSet<long> Nines = new();      // idx of steps currently status 9
            public bool Active;                       // any step status 2 or 8
            public readonly HashSet<long> Ignored = new(); // stale 9s orphaned by a cancel-at-prompt
            public long LastDbChangeUnix;             // newest mtime among db/-wal at last read
        }

        /// <summary>One poll pass over every antigravity session file. Returns how many live session
        /// files remain (drives the daemon's idle-exit).</summary>
        public int Tick(bool print)
        {
            string[] files;
            try { files = Directory.GetFiles(sessionsDir, FilePrefix + "*.json"); }
            catch { return 0; }

            bool? ideAlive = null; // lazily computed once per tick, only if some session has no pid
            int live = 0;
            foreach (string file in files)
            {
                SessionState? s = TryRead(file);
                if (s is null) { live++; continue; } // mid-write; next tick

                // Ghost cleanup: the session's IDE process is gone (Antigravity has no session-end
                // hook, so this — plus the widget's identical filter — is how ended sessions vanish).
                bool dead = s.Pid > 0
                    ? !ProcessHelper.IsAlive(s.Pid)
                    : !(ideAlive ??= AnyIdeProcessAlive());
                if (dead)
                {
                    try { File.Delete(file); } catch { }
                    string convId = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
                    _convs.Remove(convId);
                    Log($"{convId}: IDE gone — session file removed");
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

            if (s.State == "red")
            {
                if (newNines == 0)
                {
                    // The prompt was answered: approve (9→2, a step now executes) resumes yellow at
                    // this very instant; deny (9→7) clears — yellow if the model is already
                    // responding, green otherwise (Stop will confirm either way).
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
                // on Stop is an orphan from a cancel at the prompt — remember it, never red on it.
                // (A REAL new prompt writes the db AFTER the Stop, so it always passes this gate.)
                if (s.State == "green" && s.Event == "Stop" && conv.LastDbChangeUnix <= s.Ts)
                {
                    conv.Ignored.UnionWith(conv.Nines);
                    Log($"{id}: ignoring {conv.Nines.Count} stale 9(s) left behind before Stop");
                    if (print) Console.WriteLine($"{id}: {conv.Nines.Count} stale 9(s) predate Stop — ignored, stays {s.State}");
                    return;
                }
                Write(file, s, "red", "ApprovalPending");
                Log($"{id}: {s.State} -> red (approval prompt showing)");
                if (print) Console.WriteLine($"{id}: {s.State} -> red (approval prompt showing)");
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

        private static bool AnyIdeProcessAlive()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    string n = p.ProcessName;
                    if (n.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("language_server", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
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

                var poller = new Poller(sessions, convs, tmp);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Turn running (PreInvocation wrote yellow), model streaming: stays yellow.
                PlantSession(sessionFile, "yellow", "PreInvocation", Environment.ProcessId, now);
                poller.Tick(print: false);
                all &= Check("streaming turn stays yellow", ReadState(sessionFile) is ("yellow", "PreInvocation"));

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

                // Mid-turn gap: every step momentarily completed — must NOT write green (Stop's job).
                Exec(conn, "UPDATE steps SET status = 3");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("all-completed mid-turn gap does NOT go green",
                    ReadState(sessionFile) is ("yellow", "ApprovalResolved"));

                // DENY path: prompt shows (red), user denies (9→7) → clears immediately.
                Exec(conn, "INSERT INTO steps VALUES (4, 1, 9)");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("second prompt -> red again", ReadState(sessionFile) is ("red", "ApprovalPending"));
                Exec(conn, "UPDATE steps SET status = 7 WHERE \"idx\" = 4");
                Touch(db);
                poller.Tick(print: false);
                all &= Check("deny (9->7) clears red (nothing running -> green)",
                    ReadState(sessionFile) is ("green", "ApprovalResolved"));

                // Cancel-at-prompt ghost: a 9 lands, then Stop fires (cancel) leaving it behind.
                // The idle green session must NOT be re-reddened by the dead prompt.
                Exec(conn, "INSERT INTO steps VALUES (5, 1, 9)");
                Touch(db);
                PlantSession(sessionFile, "green", "Stop", Environment.ProcessId,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2); // Stop written after that db change
                poller.Tick(print: false);
                all &= Check("stale 9 left by cancel-at-prompt stays green",
                    ReadState(sessionFile) is ("green", "Stop"));

                // ...but a genuinely NEW prompt (db written AFTER the Stop) still reds.
                Exec(conn, "INSERT INTO steps VALUES (6, 1, 9)");
                File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddSeconds(10));
                string wal = db + "-wal";
                if (File.Exists(wal)) File.SetLastWriteTimeUtc(wal, DateTime.UtcNow.AddSeconds(10));
                poller.Tick(print: false);
                all &= Check("NEW 9 written after Stop -> red (stale-ignore doesn't overreach)",
                    ReadState(sessionFile) is ("red", "ApprovalPending"));
                Exec(conn, "UPDATE steps SET status = 6 WHERE \"idx\" IN (5, 6)"); // cancel both
                Touch(db);
                poller.Tick(print: false);
                all &= Check("cancelled prompts (9->6) clear red",
                    ReadState(sessionFile) is ("green", "ApprovalResolved"));

                // Ghost cleanup: the IDE process is gone -> the session file is removed.
                PlantSession(sessionFile, "green", "Stop", FindDeadPid(), now);
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
