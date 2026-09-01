using System;
using System.Collections.Generic;
using Godot;
using Serika.Net.Codec;

namespace SerikaSocial.Player;

/// Headless voice-pipeline diagnostic.
///
///   Godot --headless --path game -- --serika-voicetest
///
/// Voice is the hardest subsystem in this client to check by ear and the easiest to be wrong
/// about quietly. The bug this diagnostic exists to catch had shipped for the whole life of the
/// feature: `AudioEffectCapture` returns frames at the **AudioServer mix rate** (44100 Hz by
/// default), and `VoiceManager` took 320 of them, called that "20 ms of 16 kHz audio", and pushed
/// it into a generator declared at 16 kHz. Nothing errored. Every number in isolation looked
/// plausible. What came out the other end was every speaker pitched down to 0.36x — a slow,
/// unintelligible rumble — while the sender emitted frames 2.76x faster than intended, at
/// ~88 KB/s, over the relay's 64 KB/s per-peer budget.
///
/// The decisive measurement is PITCH, not sample counts: feed a known sine in, measure the tone
/// that comes out. A rate bug is invisible to a count-based test that scales its own expectation
/// by the same wrong ratio, and it is unmistakable as "I sent 440 Hz and got 159 Hz".
///
/// Phases:
///   RATE     — output sample count matches the 16 kHz target for a known duration of input.
///   PITCH    — a 440 Hz sine in is a 440 Hz sine out, measured by zero-crossing rate.
///   CADENCE  — one second of speech-level audio produces ~50 frames, not ~137.
///   BUDGET   — the resulting bitrate fits the relay's per-peer bandwidth budget.
///   GATE     — VAD opens on speech, closes on silence, and push-to-talk overrides it.
///   CODEC    — a frame survives the real VoiceFrame.Encode/Decode round trip byte-for-byte.
public static class VoiceDiagnostic
{
    /// The relay's per-peer budget (server/instanced/src/server.rs BW_BUDGET_BPS).
    private const int BudgetBytesPerSec = 64 * 1024;

    public static void Run(Node host)
    {
        int failures = 0;
        var vm = new VoiceManager { Name = "VoiceDiagTarget" };
        host.AddChild(vm);

        // Exercise the rates that actually occur: Godot's default, the common 48k pro rate, and
        // the degenerate case where the device already runs at the wire rate.
        foreach (int rate in new[] { 44100, 48000, 16000 })
        {
            failures += RatePhase(vm, rate);
            failures += PitchPhase(vm, rate);
            failures += CadencePhase(vm, rate);
        }

        failures += GatePhase(vm);
        failures += CodecPhase();
        failures += MixPhase();

        GD.Print(failures == 0
            ? "VOICETEST PASS — all phases green"
            : $"VOICETEST FAIL — {failures} check(s) failed");
        host.GetTree().Quit(failures == 0 ? 0 : 1);
    }

    // ── Phases ────────────────────────────────────────────────────────────────────────

    /// One second of input at `rate` must yield one second of output at 16 kHz.
    private static int RatePhase(VoiceManager vm, int rate)
    {
        var frames = Collect(vm, rate, Sine(rate, 1.0, 440, 0.3f));
        int samples = frames.Count * 320;
        // Allow a frame of slack: the tail that doesn't fill a whole 20 ms frame is held back.
        int expected = VoiceManager.SampleRate;
        bool ok = Math.Abs(samples - expected) <= 320;
        GD.Print($"VOICETEST RATE {rate}Hz -> in 1.000s, out {samples / (float)VoiceManager.SampleRate:F3}s " +
                 $"({samples} samples, want ~{expected}) {(ok ? "OK" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// The actual bug. A 440 Hz tone must still be 440 Hz after resampling; under the old code it
    /// arrived at 440 * 16000/44100 = 160 Hz.
    private static int PitchPhase(VoiceManager vm, int rate)
    {
        const float tone = 440f;
        var frames = Collect(vm, rate, Sine(rate, 1.0, tone, 0.3f));
        if (frames.Count == 0)
        {
            GD.Print($"VOICETEST PITCH {rate}Hz -> no frames emitted FAIL");
            return 1;
        }

        float measured = DominantHz(frames);
        // Zero-crossing pitch on a pure tone is accurate to well under 5%.
        bool ok = Math.Abs(measured - tone) / tone < 0.05f;
        GD.Print($"VOICETEST PITCH {rate}Hz -> sent {tone:F0}Hz, got {measured:F1}Hz " +
                 $"({measured / tone:F3}x) {(ok ? "OK" : "FAIL")}");
        if (!ok)
            GD.Print($"    a ratio near {VoiceManager.SampleRate / (float)rate:F3} means the capture " +
                     "rate is being ignored — the original defect");
        return ok ? 0 : 1;
    }

    /// Frame cadence drives bandwidth. 20 ms frames means 50/s; the rate bug produced ~137/s.
    private static int CadencePhase(VoiceManager vm, int rate)
    {
        var frames = Collect(vm, rate, Sine(rate, 1.0, 440, 0.3f));
        int n = frames.Count;
        bool cadenceOk = n >= 48 && n <= 51;

        // 4-byte VoiceFrame header + 1 type byte + 4 peer-id bytes on the relay's re-frame.
        int bytesPerSec = n * (320 * 2 + 4 + 5);
        bool budgetOk = bytesPerSec < BudgetBytesPerSec;

        GD.Print($"VOICETEST CADENCE {rate}Hz -> {n} frames/s (want 50) {(cadenceOk ? "OK" : "FAIL")}");
        GD.Print($"VOICETEST BUDGET  {rate}Hz -> {bytesPerSec / 1024f:F1} KB/s vs " +
                 $"{BudgetBytesPerSec / 1024} KB/s budget {(budgetOk ? "OK" : "FAIL")}");
        return (cadenceOk ? 0 : 1) + (budgetOk ? 0 : 1);
    }

    /// VAD must open on speech and close on silence, and push-to-talk must veto both.
    private static int GatePhase(VoiceManager vm)
    {
        int fails = 0;

        vm.Mode = MicMode.Open;
        var loud = Collect(vm, 48000, Sine(48000, 0.5, 300, 0.4f));
        bool opened = loud.Count > 0;
        GD.Print($"VOICETEST GATE open/speech -> {loud.Count} frames {(opened ? "OK" : "FAIL")}");
        if (!opened) fails++;

        // Near-silence, well under the gate. Some hangover frames from the previous phase are
        // expected, so allow a few rather than demanding zero.
        var quiet = Collect(vm, 48000, Sine(48000, 0.5, 300, 0.0008f));
        bool closed = quiet.Count <= 15;
        GD.Print($"VOICETEST GATE open/silence -> {quiet.Count} frames (want <=15) {(closed ? "OK" : "FAIL")}");
        if (!closed) fails++;

        // Push-to-talk with the key up must emit nothing however loud the input is.
        vm.Mode = MicMode.PushToTalk;
        vm.PushToTalkHeld = false;
        var ptt = Collect(vm, 48000, Sine(48000, 0.5, 300, 0.4f));
        bool vetoed = ptt.Count == 0;
        GD.Print($"VOICETEST GATE ptt/released -> {ptt.Count} frames (want 0) {(vetoed ? "OK" : "FAIL")}");
        if (!vetoed) fails++;

        vm.PushToTalkHeld = true;
        var pttDown = Collect(vm, 48000, Sine(48000, 0.5, 300, 0.4f));
        bool passed = pttDown.Count > 0;
        GD.Print($"VOICETEST GATE ptt/held -> {pttDown.Count} frames {(passed ? "OK" : "FAIL")}");
        if (!passed) fails++;

        vm.Mode = MicMode.Open;
        return fails;
    }

    /// The wire format the relay forwards opaquely.
    private static int CodecPhase()
    {
        var payload = new byte[320 * 2];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 % 251);

        var encoded = new VoiceFrame { Sequence = 42, Rms = 200, Payload = payload }.Encode();
        VoiceFrame back;
        try { back = VoiceFrame.Decode(encoded); }
        catch (Exception e)
        {
            GD.Print($"VOICETEST CODEC -> decode threw {e.GetType().Name} FAIL");
            return 1;
        }

        bool ok = back.Sequence == 42 && back.Rms == 200 && back.Payload.Length == payload.Length;
        if (ok)
            for (int i = 0; i < payload.Length; i++)
                if (back.Payload[i] != payload[i]) { ok = false; break; }

        GD.Print($"VOICETEST CODEC -> {encoded.Length}B on the wire for a 20ms frame, " +
                 $"round trip {(ok ? "exact OK" : "MISMATCH FAIL")}");
        return ok ? 0 : 1;
    }

    /// The bus graph behind the per-category mixer. Checks the shipping `AudioBuses` code, not a
    /// re-implementation: every category exists, routes to Master, and responds to volume/mute.
    ///
    /// The routing assertion is the one that matters. A bus that exists but sends to the wrong
    /// place still plays sound — it just ignores its own slider, which looks like "the volume
    /// setting does nothing" and is invisible until someone drags it.
    private static int MixPhase()
    {
        int fails = 0;
        SerikaSocial.Audio.AudioBuses.Ensure();

        foreach (var bus in SerikaSocial.Audio.AudioBuses.Categories)
        {
            int idx = AudioServer.GetBusIndex(bus);
            if (idx < 0)
            {
                GD.Print($"VOICETEST MIX {bus} -> missing FAIL");
                fails++;
                continue;
            }
            string send = AudioServer.GetBusSend(idx);
            bool routed = send == SerikaSocial.Audio.AudioBuses.Master;
            GD.Print($"VOICETEST MIX {bus} -> bus {idx} sends to '{send}' {(routed ? "OK" : "FAIL")}");
            if (!routed) fails++;
        }

        // The Record bus must NOT be under Master, or the player hears their own microphone.
        int rec = AudioServer.GetBusIndex("Record");
        if (rec > 0)
        {
            bool isolated = AudioServer.IsBusMute(rec);
            GD.Print($"VOICETEST MIX Record -> muted={isolated} {(isolated ? "OK" : "FAIL (mic would echo)")}" );
            if (!isolated) fails++;
        }

        // Volume + mute round trip on one representative bus.
        const string probe = SerikaSocial.Audio.AudioBuses.World;
        int pi = AudioServer.GetBusIndex(probe);
        SerikaSocial.Audio.AudioBuses.SetVolume(probe, 0.5f);
        float db = AudioServer.GetBusVolumeDb(pi);
        bool volOk = Math.Abs(db - Mathf.LinearToDb(0.5f)) < 0.01f && !AudioServer.IsBusMute(pi);
        GD.Print($"VOICETEST MIX volume 50% -> {db:F2} dB {(volOk ? "OK" : "FAIL")}");
        if (!volOk) fails++;

        SerikaSocial.Audio.AudioBuses.SetVolume(probe, 0f);
        bool muteOk = AudioServer.IsBusMute(pi);
        GD.Print($"VOICETEST MIX volume 0% -> muted={muteOk} {(muteOk ? "OK" : "FAIL")}");
        if (!muteOk) fails++;
        SerikaSocial.Audio.AudioBuses.SetVolume(probe, 1f);

        // Night mode adds and removes exactly one compressor, rather than stacking one per toggle.
        int master = AudioServer.GetBusIndex(SerikaSocial.Audio.AudioBuses.Master);
        SerikaSocial.Audio.AudioBuses.SetNightMode(true);
        SerikaSocial.Audio.AudioBuses.SetNightMode(true);
        int comps = 0;
        for (int i = 0; i < AudioServer.GetBusEffectCount(master); i++)
            if (AudioServer.GetBusEffect(master, i) is AudioEffectCompressor) comps++;
        bool oneComp = comps == 1;
        GD.Print($"VOICETEST MIX night mode on x2 -> {comps} compressor(s) {(oneComp ? "OK" : "FAIL")}");
        if (!oneComp) fails++;

        SerikaSocial.Audio.AudioBuses.SetNightMode(false);
        comps = 0;
        for (int i = 0; i < AudioServer.GetBusEffectCount(master); i++)
            if (AudioServer.GetBusEffect(master, i) is AudioEffectCompressor) comps++;
        bool cleared = comps == 0;
        GD.Print($"VOICETEST MIX night mode off -> {comps} compressor(s) {(cleared ? "OK" : "FAIL")}");
        if (!cleared) fails++;

        return fails;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    /// Run `mono` through the real pipeline and collect every emitted frame.
    private static List<byte[]> Collect(VoiceManager vm, int rate, float[] mono)
    {
        var got = new List<byte[]>();
        void OnFrame(byte[] pcm) => got.Add(pcm);

        vm.ResetForDiagnostics();
        vm.VoiceFrameReady += OnFrame;
        // Feed in 10 ms slices so the resampler's cross-call cursor is genuinely exercised —
        // one big block would hide a cursor that resets and silently drops a sample per call.
        int slice = Math.Max(1, rate / 100);
        for (int off = 0; off < mono.Length; off += slice)
        {
            int n = Math.Min(slice, mono.Length - off);
            var chunk = new float[n];
            Array.Copy(mono, off, chunk, 0, n);
            vm.PumpForDiagnostics(chunk, rate);
        }
        vm.VoiceFrameReady -= OnFrame;
        return got;
    }

    private static float[] Sine(int rate, double seconds, double hz, float amp)
    {
        int n = (int)(rate * seconds);
        var buf = new float[n];
        for (int i = 0; i < n; i++) buf[i] = (float)(Math.Sin(2 * Math.PI * hz * i / rate) * amp);
        return buf;
    }

    /// Pitch by zero-crossing rate over the concatenated PCM16 frames. Exact enough for a pure
    /// tone and, unlike an FFT, has no windowing subtleties to get wrong in a test.
    private static float DominantHz(List<byte[]> frames)
    {
        int crossings = 0, total = 0;
        short prev = 0;
        bool first = true;

        foreach (var f in frames)
        {
            for (int i = 0; i < f.Length / 2; i++)
            {
                short s = (short)(f[i * 2] | (f[i * 2 + 1] << 8));
                if (!first && ((s > 0 && prev <= 0) || (s <= 0 && prev > 0))) crossings++;
                prev = s;
                first = false;
                total++;
            }
        }
        if (total == 0) return 0f;
        // Two zero crossings per cycle.
        return crossings / 2f * VoiceManager.SampleRate / total;
    }
}
