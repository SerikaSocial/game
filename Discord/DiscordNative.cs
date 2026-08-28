using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace SerikaSocial.Discord;

/// <summary>
/// P/Invoke surface of the native Discord Social SDK (the C ABI in cdiscord.h, shipped in
/// ref/DiscordSocialSdk). Every declaration here is transcribed from that header — the ABI is
/// opaque single-pointer structs plus free functions, so nothing is marshaled by guesswork.
///
/// Library resolution never relies on the OS loader alone: <see cref="EnsureLoaded"/> picks an
/// explicit candidate (env override → beside the executable → extracted from the pck) and pins
/// it with a DllImportResolver, so an exported build finds the copy inside its own install dir
/// even when no system-wide copy exists.
/// </summary>
internal static unsafe class DiscordNative
{
    /// The library name every DllImport below refers to; resolved by the resolver we install.
    internal const string LibName = "discord_partner_sdk";

    /// Version of the SDK binaries bundled under bin/. Bump when swapping the ref/ drop — the
    /// extracted copy in user:// is stamped with this so an SDK upgrade replaces a stale file.
    internal const string SdkVersion = "1.10.18687";

    // ── Blittable mirrors of the C structs ────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct Discord_String
    {
        public IntPtr ptr;   // uint8_t* — NOT null-terminated; length is `size`
        public nuint size;
    }

    // Every SDK object is an opaque single pointer (Discord_Call is opaque[3] but we never
    // touch calls). Keeping them as distinct types stops one handle being passed to another
    // object's functions.
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_Client { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_Activity { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ActivityAssets { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ActivityTimestamps { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ActivityParty { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ActivitySecrets { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ActivityButton { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_ClientResult { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_AuthorizationArgs { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_AuthorizationCodeVerifier { public IntPtr opaque; }
    [StructLayout(LayoutKind.Sequential)] internal struct Discord_AuthorizationCodeChallenge { public IntPtr opaque; }

    // ── Enums (values from cdiscord.h; C enums marshal as int) ────────────────────────

    internal enum ClientStatus { Disconnected = 0, Connecting = 1, Connected = 2, Ready = 3, Reconnecting = 4, Disconnecting = 5, HttpWait = 6 }
    internal enum ClientError { None = 0, ConnectionFailed = 1, UnexpectedClose = 2, ConnectionCanceled = 3 }
    internal enum ErrorType { None = 0, NetworkError = 1, HTTPError = 2, ClientNotReady = 3, Disabled = 4, ClientDestroyed = 5, ValidationError = 6, Aborted = 7, AuthorizationFailed = 8, RPCError = 9 }
    internal enum LoggingSeverity { Verbose = 1, Info = 2, Warning = 3, Error = 4, None = 5 }
    internal enum ActivityTypes { Playing = 0 }
    internal enum ActivityPartyPrivacy { Private = 0, Public = 1 }
    internal enum ActivityActionTypes { Invalid = 0, Join = 1, JoinRequest = 5 }
    internal enum AuthorizationTokenType { User = 0, Bearer = 1 }
    internal enum IntegrationType { GuildInstall = 0, UserInstall = 1 }

    // ── Callback shapes (Cdecl — the header declares plain C, not stdcall) ────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnStatusChanged(ClientStatus status, ClientError error, int errorDetail, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnActivityJoin(Discord_String joinSecret, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OnActivityInvite(ref Discord_ActivityInvite invite, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UpdateRichPresenceCb(ref Discord_ClientResult result, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCb(Discord_String message, LoggingSeverity severity, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NoArgCb(IntPtr userData);

    // Auth bootstrap: result pointer is SDK-owned (read, never drop); strings arrive by value
    // and their buffers must be released with Discord_Free.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AuthorizationCb(ref Discord_ClientResult result, Discord_String code, Discord_String redirectUri, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TokenExchangeCb(ref Discord_ClientResult result, Discord_String accessToken, Discord_String refreshToken, AuthorizationTokenType tokenType, int expiresIn, Discord_String scopes, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UpdateTokenCb(ref Discord_ClientResult result, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Discord_ActivityInvite { public IntPtr opaque; }

    // ── Raw imports (global lifecycle) ────────────────────────────────────────────────

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Free(IntPtr ptr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_RunCallbacks();

    // ── Raw imports (client lifecycle) ────────────────────────────────────────────────

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_Init(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_Drop(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetApplicationId(ref Discord_Client self, ulong applicationId);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong Discord_Client_GetApplicationId(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_Connect(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_Disconnect(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ClientStatus Discord_Client_GetStatus(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Discord_Client_GetVersionMajor();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Discord_Client_GetVersionMinor();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Discord_Client_GetVersionPatch();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_GetVersionHash(ref Discord_String returnValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_StatusToString(ClientStatus type, ref Discord_String returnValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_AddLogCallback(ref Discord_Client self, LogCb callback, IntPtr callbackFree, IntPtr userData, LoggingSeverity minSeverity);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetStatusChangedCallback(ref Discord_Client self, OnStatusChanged cb, IntPtr cbFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetActivityJoinCallback(ref Discord_Client self, OnActivityJoin cb, IntPtr cbFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetActivityInviteCreatedCallback(ref Discord_Client self, OnActivityInvite cb, IntPtr cbFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_UpdateRichPresence(ref Discord_Client self, ref Discord_Activity activity, UpdateRichPresenceCb cb, IntPtr cbFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_ClearRichPresence(ref Discord_Client self);

    // ── Raw imports (auth bootstrap) ──────────────────────────────────────────────────

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_GetDefaultPresenceScopes(ref Discord_String returnValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_CreateAuthorizationCodeVerifier(ref Discord_Client self, ref Discord_AuthorizationCodeVerifier returnValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_Authorize(ref Discord_Client self, ref Discord_AuthorizationArgs args, AuthorizationCb callback, IntPtr callbackFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_AbortAuthorize(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_GetToken(ref Discord_Client self, ulong applicationId, Discord_String code, Discord_String codeVerifier, Discord_String redirectUri, TokenExchangeCb callback, IntPtr callbackFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_UpdateToken(ref Discord_Client self, AuthorizationTokenType tokenType, Discord_String token, UpdateTokenCb callback, IntPtr callbackFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_RefreshToken(ref Discord_Client self, ulong applicationId, Discord_String refreshToken, TokenExchangeCb callback, IntPtr callbackFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetTokenExpirationCallback(ref Discord_Client self, NoArgCb cb, IntPtr cbFree, IntPtr userData);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern bool Discord_Client_IsAuthenticated(ref Discord_Client self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_SetGameWindowPid(ref Discord_Client self, int pid);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_Init(ref Discord_AuthorizationArgs self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_Drop(ref Discord_AuthorizationArgs self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_SetClientId(ref Discord_AuthorizationArgs self, ulong value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_SetScopes(ref Discord_AuthorizationArgs self, Discord_String value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_SetCodeChallenge(ref Discord_AuthorizationArgs self, Discord_AuthorizationCodeChallenge* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationArgs_SetIntegrationType(ref Discord_AuthorizationArgs self, IntegrationType* value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationCodeVerifier_Drop(ref Discord_AuthorizationCodeVerifier self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationCodeVerifier_Verifier(ref Discord_AuthorizationCodeVerifier self, ref Discord_String returnValue);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationCodeVerifier_Challenge(ref Discord_AuthorizationCodeVerifier self, ref Discord_AuthorizationCodeChallenge returnValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationCodeChallenge_Init(ref Discord_AuthorizationCodeChallenge self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_AuthorizationCodeChallenge_Drop(ref Discord_AuthorizationCodeChallenge self);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Discord_Client_RegisterLaunchCommand")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Discord_Client_RegisterLaunchCommand(ref Discord_Client self, ulong applicationId, Discord_String command);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Discord_Client_IsDiscordAppInstalled(ref Discord_Client self, IsDiscordAppInstalledCb cb, IntPtr cbFree, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void IsDiscordAppInstalledCb([MarshalAs(UnmanagedType.I1)] bool installed, IntPtr userData);

    // ── Raw imports (activity tree) ───────────────────────────────────────────────────

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_Init(ref Discord_Activity self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_Drop(ref Discord_Activity self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetName(ref Discord_Activity self, Discord_String value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetType(ref Discord_Activity self, ActivityTypes value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetState(ref Discord_Activity self, Discord_String* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetDetails(ref Discord_Activity self, Discord_String* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetTimestamps(ref Discord_Activity self, Discord_ActivityTimestamps* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetParty(ref Discord_Activity self, Discord_ActivityParty* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetSecrets(ref Discord_Activity self, Discord_ActivitySecrets* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_SetAssets(ref Discord_Activity self, Discord_ActivityAssets* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_Activity_AddButton(ref Discord_Activity self, ref Discord_ActivityButton button);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_Init(ref Discord_ActivityAssets self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_Drop(ref Discord_ActivityAssets self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_SetLargeImage(ref Discord_ActivityAssets self, Discord_String* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_SetLargeText(ref Discord_ActivityAssets self, Discord_String* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_SetSmallImage(ref Discord_ActivityAssets self, Discord_String* value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityAssets_SetSmallText(ref Discord_ActivityAssets self, Discord_String* value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityTimestamps_Init(ref Discord_ActivityTimestamps self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityTimestamps_Drop(ref Discord_ActivityTimestamps self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityTimestamps_SetStart(ref Discord_ActivityTimestamps self, ulong value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityTimestamps_SetEnd(ref Discord_ActivityTimestamps self, ulong value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_Init(ref Discord_ActivityParty self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_Drop(ref Discord_ActivityParty self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_SetId(ref Discord_ActivityParty self, Discord_String value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_SetCurrentSize(ref Discord_ActivityParty self, int value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_SetMaxSize(ref Discord_ActivityParty self, int value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityParty_SetPrivacy(ref Discord_ActivityParty self, ActivityPartyPrivacy value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivitySecrets_Init(ref Discord_ActivitySecrets self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivitySecrets_Drop(ref Discord_ActivitySecrets self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivitySecrets_SetJoin(ref Discord_ActivitySecrets self, Discord_String value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityButton_Init(ref Discord_ActivityButton self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityButton_Drop(ref Discord_ActivityButton self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityButton_SetLabel(ref Discord_ActivityButton self, Discord_String value);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ActivityButton_SetUrl(ref Discord_ActivityButton self, Discord_String value);

    // ── Raw imports (client result — read-only accessors; instances we see are owned by
    //    the SDK and must NOT be dropped) ─────────────────────────────────────────────

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern bool Discord_ClientResult_Successful(ref Discord_ClientResult self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern ErrorType Discord_ClientResult_Type(ref Discord_ClientResult self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern int Discord_ClientResult_ErrorCode(ref Discord_ClientResult self);
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)] internal static extern void Discord_ClientResult_ToString(ref Discord_ClientResult self, ref Discord_String returnValue);

    // ── String marshaling ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a Discord_String the SDK filled in for us. Its buffer belongs to the SDK and MUST
    /// be released with Discord_Free — discordpp.h's own inline wrappers do exactly this after
    /// copying. Returns null for empty strings.
    /// </summary>
    internal static string ReadAndFree(ref Discord_String s)
    {
        if (s.ptr == IntPtr.Zero || s.size == 0)
        {
            if (s.ptr != IntPtr.Zero) Discord_Free(s.ptr);
            s = default;
            return null;
        }
        byte[] buf = new byte[(int)s.size];
        Marshal.Copy(s.ptr, buf, 0, buf.Length);
        Discord_Free(s.ptr);
        s = default;
        return Encoding.UTF8.GetString(buf);
    }

    /// Pointer-taking setter for the optional-string helpers — Action&lt;T&gt; can't carry a
    /// pointer type argument, so this exists purely to give the lambda a shape.
    internal delegate void OptStrSetter(Discord_String* value);

    /// <summary>
    /// Feed a managed string to a setter that takes Discord_String by value, via a pinned
    /// UTF-8 buffer. The SDK copies synchronously, so the pin only has to live for the call.
    /// </summary>
    internal static void WithStr(string value, Action<Discord_String> setter)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        fixed (byte* p = bytes)
        {
            setter(new Discord_String { ptr = (IntPtr)p, size = (nuint)bytes.Length });
        }
    }

    /// <summary>Feed a managed string to a setter taking an OPTIONAL Discord_String* — the
    /// setter receives null when the value is absent (the C API uses the pointer itself as
    /// the option flag, so a null-clearing call needs no buffer at all).</summary>
    internal static void WithOptStr(string value, OptStrSetter setter)
    {
        if (value == null) { setter(null); return; }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = bytes)
        {
            var s = new Discord_String { ptr = (IntPtr)p, size = (nuint)bytes.Length };
            setter(&s);
        }
    }

    // ── Library loading ───────────────────────────────────────────────────────────────

    private static IntPtr _handle;
    private static bool _resolverInstalled;
    private static bool _loadAttempted;
    private static string _loadedFrom;
    private static string _loadFailure;

    /// True when the native library is loaded and P/Invokes will resolve.
    internal static bool Loaded => _handle != IntPtr.Zero;
    internal static string LoadedFrom => _loadedFrom;
    internal static string LoadFailure => _loadFailure;

    /// <summary>
    /// Locate and load the platform's SDK library. Order:
    ///   1. SERIKA_DISCORD_LIB env var (absolute path — dev override, also used by the diag),
    ///   2. a copy sitting next to the game executable (manual/sideload installs),
    ///   3. user://bin/&lt;name stamped with the SDK version&gt;, extracted on demand from the
    ///      res://bin/ copy the export packs into the pck (the same trick VideoManager uses
    ///      for ffmpeg — dlopen cannot read inside a pck, so the file must be materialized),
    ///   4. plain OS probing as a last resort.
    /// Returns false (never throws) when the library can't be found — Discord presence is an
    /// optional feature and must not take the game down with it.
    /// </summary>
    internal static bool EnsureLoaded()
    {
        if (_handle != IntPtr.Zero) return true;
        if (_loadAttempted) return false;
        _loadAttempted = true;

        try
        {
            // The Android SDK ships as an AAR with Java glue — a plain dlopen story doesn't
            // exist there yet, so the feature is desktop-only for now.
            if (OS.HasFeature("android"))
            {
                _loadFailure = "android builds don't bundle the desktop SDK library";
                return false;
            }

            string fileName = NativeFileName();
            string tried = "";

            // 1. explicit override
            string env = OS.GetEnvironment("SERIKA_DISCORD_LIB");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                _handle = NativeLibrary.Load(env);
                _loadedFrom = env;
            }

            // 2. beside the executable
            if (_handle == IntPtr.Zero)
            {
                try
                {
                    string exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
                    string beside = Path.Combine(exeDir ?? "", fileName);
                    tried += $" {beside}";
                    if (File.Exists(beside))
                    {
                        _handle = NativeLibrary.Load(beside);
                        _loadedFrom = beside;
                    }
                }
                catch { }
            }

            // 3. extracted from the pck (version-stamped so SDK upgrades replace stale copies)
            if (_handle == IntPtr.Zero)
            {
                try
                {
                    string extracted = ExtractFromPck(fileName);
                    tried += $" {extracted ?? "(res://bin/" + fileName + " missing)"}";
                    if (extracted != null)
                    {
                        _handle = NativeLibrary.Load(extracted);
                        _loadedFrom = extracted;
                    }
                }
                catch (Exception e)
                {
                    tried += $" (extract failed: {e.Message})";
                }
            }

            // 4. OS default probing (e.g. a system-wide install)
            if (_handle == IntPtr.Zero && NativeLibrary.TryLoad(LibName, out IntPtr h))
            {
                _handle = h;
                _loadedFrom = "system search path";
            }

            if (_handle == IntPtr.Zero)
            {
                _loadFailure = $"no loadable {fileName}; tried:{tried}";
                return false;
            }

            InstallResolver();
            return true;
        }
        catch (Exception e)
        {
            _loadFailure = e.Message;
            return false;
        }
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(DiscordNative).Assembly, (name, _, _) =>
            name == LibName ? _handle : IntPtr.Zero);
    }

    private static string NativeFileName() => OS.GetName() switch
    {
        "Windows" => "discord_partner_sdk.dll",
        "macOS" => "libdiscord_partner_sdk.dylib",
        _ => "libdiscord_partner_sdk.so",
    };

    /// <summary>
    /// Materialize res://bin/&lt;fileName&gt; (packed in the export's pck) as a real file under
    /// user://bin/ so the dynamic loader can open it. The target name carries the SDK version:
    /// swapping the bundled binary then replaces the extracted copy on the next boot, which a
    /// plain "skip if it exists" copy (the ffmpeg strategy) would silently refuse to do.
    /// </summary>
    private static string ExtractFromPck(string fileName)
    {
        string stamped = $"{Path.GetFileNameWithoutExtension(fileName)}.{SdkVersion}{Path.GetExtension(fileName)}";
        string resPath = $"res://bin/{fileName}";
        if (!Godot.FileAccess.FileExists(resPath)) return null;

        string userBin = ProjectSettings.GlobalizePath("user://bin");
        Directory.CreateDirectory(userBin);
        string absTarget = Path.Combine(userBin, stamped);

        // Re-extract only when missing or a different size (cheap existence+length check —
        // the pck copy is 10–25 MB and hashing it every boot is pointless).
        using var src = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        if (src == null) return null;
        long srcLen = (long)src.GetLength();
        if (File.Exists(absTarget))
        {
            try
            {
                if (new FileInfo(absTarget).Length == srcLen) return absTarget;
            }
            catch { }
            try { File.Delete(absTarget); } catch { }
        }

        using var dst = Godot.FileAccess.Open($"user://bin/{stamped}", Godot.FileAccess.ModeFlags.Write);
        if (dst == null) return null;
        const int chunk = 4 * 1024 * 1024;
        while (!src.EofReached())
        {
            var buf = src.GetBuffer(chunk);
            if (buf == null || buf.Length == 0) break;
            dst.StoreBuffer(buf);
        }
        return absTarget;
    }
}
