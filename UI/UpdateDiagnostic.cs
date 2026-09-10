using System;
using System.Diagnostics;
using System.IO;
using Godot;

namespace SerikaSocial;

/// Headless check on the one part of the updater that decides whether an install survives:
/// the swap. Run with `--serika-updatetest`.
///
/// This exists because the previous implementation **could not work on Windows and nothing
/// caught it.** It copied the whole archive over the live install while the game was running,
/// which throws on the memory-mapped `.pck` and runtime DLLs partway through and leaves an
/// unbootable mixture of old and new files. Reading the code did not catch it; only running the
/// swap does. So the swap is now driven here, from the real `Updater` code paths — never a
/// re-implementation, which would only test itself.
///
/// What this can and cannot prove, stated plainly: on Linux it runs the real apply script and
/// checks the resulting tree. It CANNOT reproduce Windows file locking (wine cannot run this
/// game at all, per the notes in CLAUDE.md), so the Windows script is verified structurally —
/// that it waits on the PID, that it uses robocopy with retries, and that it never reports
/// success on a robocopy failure. The Windows swap itself still needs one run on real hardware.
public static class UpdateDiagnostic
{
    private static int _fail;

    public static void Run()
    {
        GD.Print("UPDATETEST start");

        string root = Path.Combine(Path.GetTempPath(), "serika-updatetest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            PayloadRootPhase(root);
            MirrorPhase(root);
            WindowsScriptPhase(root);
        }
        catch (Exception e)
        {
            Fail("EXCEPTION", e.ToString());
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        GD.Print(_fail == 0 ? "UPDATETEST PASS" : $"UPDATETEST FAIL ({_fail} check(s))");
        (Engine.GetMainLoop() as SceneTree)?.Quit(_fail == 0 ? 0 : 1);
    }

    // ── PAYLOAD: a zip that wraps the build in one folder must not nest the install ──────

    private static void PayloadRootPhase(string root)
    {
        // Flat archive: files at the top level.
        string flat = Path.Combine(root, "flat");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, "SerikaSocial.x86_64"), "exe");
        Check("PAYLOAD flat", Updater.ResolvePayloadRoot(flat, "/install/SerikaSocial.x86_64") == flat);

        // Wrapped archive: one top-level folder holding the build. Resolving to the archive root
        // here would mirror a *directory* into the install and leave the game one level deep.
        string wrapped = Path.Combine(root, "wrapped");
        string inner = Path.Combine(wrapped, "SerikaSocial-1.2.3");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "SerikaSocial.x86_64"), "exe");
        Check("PAYLOAD wrapped", Updater.ResolvePayloadRoot(wrapped, "/install/SerikaSocial.x86_64") == inner);
    }

    // ── MIRROR: the real apply script, run for real ──────────────────────────────────────

    private static void MirrorPhase(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            GD.Print("UPDATETEST MIRROR skipped (Windows path is checked structurally below)");
            return;
        }

        string install = Path.Combine(root, "install");
        string staged = Path.Combine(root, "staged");
        string tmp = Path.Combine(root, "tmp");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(staged, "nested"));
        Directory.CreateDirectory(tmp);

        string exePath = Path.Combine(install, "SerikaSocial.x86_64");

        // The old install: an executable, a pck, and a file the new build does not ship.
        File.WriteAllText(exePath, "OLD-EXE");
        File.WriteAllText(Path.Combine(install, "SerikaSocial.pck"), "OLD-PCK");
        File.WriteAllText(Path.Combine(install, "keep-me.cfg"), "USER");

        // The new build.
        File.WriteAllText(Path.Combine(staged, "SerikaSocial.x86_64"), "NEW-EXE");
        File.WriteAllText(Path.Combine(staged, "SerikaSocial.pck"), "NEW-PCK");
        File.WriteAllText(Path.Combine(staged, "nested", "lib.so"), "NEW-LIB");

        string script = Updater.WriteUnixApplyScript(tmp, staged, install, exePath);
        Check("MIRROR script written", File.Exists(script));

        // Two neuters, everything else runs verbatim. The wait: the script polls the PID of
        // the process that WROTE it — us, still alive running this test — so it would spin its
        // whole 60 s budget before touching anything. The relaunch: the fixture is not a
        // build, so launching it would fail and take the failure path.
        string body = File.ReadAllText(script)
            .Replace("while [ $i -lt 60 ]", "while false")
            .Replace("\"$GAME\" &", "true &");
        File.WriteAllText(script, body);

        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"\"{script}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        Check("MIRROR script exit 0", p.WaitForExit(60_000) && p.ExitCode == 0,
            $"exit={p.ExitCode} {p.StandardError.ReadToEnd()}");

        // The headline: the running executable is replaced. This is precisely what the old
        // implementation skipped, and why an update left a new pck beside an old binary.
        Check("MIRROR exe replaced", Read(exePath) == "NEW-EXE", Read(exePath));
        Check("MIRROR pck replaced", Read(Path.Combine(install, "SerikaSocial.pck")) == "NEW-PCK");
        Check("MIRROR subdir copied", Read(Path.Combine(install, "nested", "lib.so")) == "NEW-LIB");

        // /E, not /MIR: files the archive does not contain are left alone. Purging the install
        // directory would take anything a player or a sibling tool put there.
        Check("MIRROR untouched file kept", Read(Path.Combine(install, "keep-me.cfg")) == "USER");

        Check("MIRROR staging removed", !Directory.Exists(staged));
        Check("MIRROR no error log", !File.Exists(Path.Combine(install, "update-error.log")));
    }

    // ── WINDOWS: structural only, and honest about it ────────────────────────────────────

    private static void WindowsScriptPhase(string root)
    {
        string tmp = Path.Combine(root, "wintmp");
        Directory.CreateDirectory(tmp);
        string s = File.ReadAllText(Updater.WriteWindowsApplyScript(
            tmp, @"C:\staged", @"C:\Program Files\Serika", @"C:\Program Files\Serika\SerikaSocial.exe"));

        // Must wait for us to exit before touching the install.
        Check("WIN waits on pid", s.Contains("tasklist /FI \"PID eq %PID%\""));
        // robocopy, because it retries locked files — the whole reason the old `copy` failed.
        Check("WIN uses robocopy", s.Contains("robocopy") && s.Contains("/R:10"));
        // /E not /MIR, same reason as the Unix path.
        Check("WIN does not purge", s.Contains("/E") && !s.Contains("/MIR"));
        // robocopy signals success with exit codes under 8, so anything >= 8 must be a failure.
        Check("WIN detects failure", s.Contains("if errorlevel 8"));
        // A failed swap must still put the player back in a working game.
        Check("WIN relaunches on failure", s.Contains("update-error.log") && s.Contains("exit /b 1"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static string Read(string p) => File.Exists(p) ? File.ReadAllText(p) : "<missing>";

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) { GD.Print($"UPDATETEST   ok   {name}"); return; }
        Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        _fail++;
        GD.PrintErr($"UPDATETEST   FAIL {name}" + (detail != null ? $" — {detail}" : ""));
    }
}
