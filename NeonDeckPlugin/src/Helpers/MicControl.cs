namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Mutes / unmutes the Windows default microphone(s) through Core Audio (IAudioEndpointVolume).
    /// This is a real device-level mute: every app, Discord included, goes silent, and the true state can be read back.
    /// Both the default "console" and default "communications" capture endpoints are handled, since Discord and games
    /// may pick different ones.
    /// </summary>
    internal static class MicControl
    {
        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            Int32 EnumAudioEndpoints(EDataFlow dataFlow, UInt32 stateMask, out IntPtr devices);
            Int32 GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            Int32 GetDevice([MarshalAs(UnmanagedType.LPWStr)] String id, out IMMDevice device);
            Int32 RegisterEndpointNotificationCallback(IntPtr client);
            Int32 UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            Int32 Activate(ref Guid iid, UInt32 clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out Object iface);
            Int32 OpenPropertyStore(UInt32 access, out IntPtr properties);
            Int32 GetId([MarshalAs(UnmanagedType.LPWStr)] out String id);
            Int32 GetState(out UInt32 state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            Int32 RegisterControlChangeNotify(IntPtr notify);
            Int32 UnregisterControlChangeNotify(IntPtr notify);
            Int32 GetChannelCount(out UInt32 count);
            Int32 SetMasterVolumeLevel(Single levelDb, ref Guid eventContext);
            Int32 SetMasterVolumeLevelScalar(Single level, ref Guid eventContext);
            Int32 GetMasterVolumeLevel(out Single levelDb);
            Int32 GetMasterVolumeLevelScalar(out Single level);
            Int32 SetChannelVolumeLevel(UInt32 channel, Single levelDb, ref Guid eventContext);
            Int32 SetChannelVolumeLevelScalar(UInt32 channel, Single level, ref Guid eventContext);
            Int32 GetChannelVolumeLevel(UInt32 channel, out Single levelDb);
            Int32 GetChannelVolumeLevelScalar(UInt32 channel, out Single level);
            Int32 SetMute([MarshalAs(UnmanagedType.Bool)] Boolean mute, ref Guid eventContext);
            Int32 GetMute([MarshalAs(UnmanagedType.Bool)] out Boolean mute);
        }

        private static readonly Guid ClsidEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        private static List<(String id, IAudioEndpointVolume volume)> DefaultCaptureEndpoints()
        {
            var list = new List<(String, IAudioEndpointVolume)>();
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidEnumerator));
            foreach (var role in new[] { ERole.eCommunications, ERole.eConsole })
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out var device) != 0 || device == null)
                {
                    continue;
                }
                device.GetId(out var id);
                if (list.Exists(e => e.Item1 == id))
                {
                    continue;
                }
                var iid = IidEndpointVolume;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume vol)
                {
                    list.Add((id, vol));
                }
            }
            return list;
        }

        /// <summary>true = at least one default microphone exists.</summary>
        public static Boolean HasMicrophone()
        {
            try
            {
                return DefaultCaptureEndpoints().Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Real mute state: true when every default microphone is muted. null when no microphone.</summary>
        public static Boolean? IsMuted()
        {
            try
            {
                var eps = DefaultCaptureEndpoints();
                if (eps.Count == 0)
                {
                    return null;
                }
                var all = true;
                foreach (var (_, vol) in eps)
                {
                    vol.GetMute(out var m);
                    all &= m;
                }
                return all;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "mic: read failed");
                return null;
            }
        }

        /// <summary>Set mute on every default microphone. Returns the state read back afterwards.</summary>
        public static Boolean? SetMuted(Boolean mute)
        {
            try
            {
                var ctx = Guid.Empty;
                foreach (var (_, vol) in DefaultCaptureEndpoints())
                {
                    vol.SetMute(mute, ref ctx);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "mic: set failed");
            }
            return IsMuted();
        }
    }
}
