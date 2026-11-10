// Prefer maximum performance for Call of Duty, through NvAPI only.
// The driver database file is never opened. Buffer sizes match the public headers
// (NVDRS_SETTING_V1 pack 4, NVDRS_APPLICATION_V4, NVDRS_PROFILE_V1):
//   setting 12320 bytes, version 77856, current dword at offset 8220
//   application 20492 bytes, version 282636, exe name at offset 8
//   profile 4116 bytes, version 69652, predefined flag at offset 4104
// Setting id 0x1057EB71 is Power management mode. Value 1 is Prefer maximum performance.
// A predefined NVIDIA profile is never deleted.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kpekpes
{
    public sealed class KpekpesNvidiaResult
    {
        public int Status;
        public long Profile;
        public int Location;
        public int Value;
        public int IsPredefined;
        public bool HadOwn;
    }

    public sealed class KpekpesNvidiaSession : IDisposable
    {
        public const int StatusOk = 0;
        public const int StatusIncompatible = -9;
        public const int StatusReload = -157;
        public const int StatusSettingNotFound = -160;
        public const int StatusProfileNotFound = -163;
        public const int StatusExeNotFound = -166;
        public const int StatusExeInUse = -167;
        public const int StatusExeAmbiguous = -182;
        public const int RefusePredefined = 1001;
        public const int PreferMax = 1;

        const int SettingBytes = 12320;
        const int SettingVersion = 77856;
        const int AppBytes = 20492;
        const int AppVersion = 282636;
        const int ProfileBytes = 4116;
        const int ProfileVersion = 69652;
        const int UnicodeBytes = 4096;
        const uint SettingId = 0x1057EB71;
        const uint IdInitialize = 0x0150E828;
        const uint IdCreateSession = 0x0694D52E;
        const uint IdDestroySession = 0xDAD9CFF8;
        const uint IdLoadSettings = 0x375DBD6B;
        const uint IdSaveSettings = 0xFCBC7E14;
        const uint IdFindApp = 0xEEE566B2;
        const uint IdGetSetting = 0x73BF8338;
        const uint IdSetSetting = 0x577DD202;
        const uint IdCreateProfile = 0xCC176068;
        const uint IdCreateApp = 0x4347A9DE;
        const uint IdDeleteApp = 0x2C694BC6;
        const uint IdDeleteProfile = 0x17093206;
        const uint IdDeleteSetting = 0xE4A26362;
        const uint IdGetProfileInfo = 0x61CD6FD6;
        const uint IdFindProfile = 0x7E4A9A0B;
        const uint IdGlobalProfile = 0x617BFF9F;

        static IntPtr _module;
        static QueryFn _query;
        static bool _initialized;

        IntPtr _session;
        StatusFn _init;
        SessionOutFn _create;
        SessionFn _destroy;
        SessionFn _load;
        SessionFn _save;
        FindAppFn _findApp;
        GetSettingFn _getSetting;
        SetSettingFn _setSetting;
        CreateProfileFn _createProfile;
        CreateAppFn _createApp;
        DeleteAppFn _deleteApp;
        DeleteProfileFn _deleteProfile;
        DeleteSettingFn _deleteSetting;
        GetProfileFn _getProfile;
        FindProfileFn _findProfile;
        GlobalFn _global;

        public bool Ok;
        public string Error;
        public int LastStatus;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr QueryFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int StatusFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SessionOutFn(IntPtr pSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SessionFn(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FindAppFn(IntPtr session, IntPtr appName, IntPtr pProfile, IntPtr pApp);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CreateProfileFn(IntPtr session, IntPtr pInfo, IntPtr pProfile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CreateAppFn(IntPtr session, IntPtr profile, IntPtr pApp);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DeleteAppFn(IntPtr session, IntPtr profile, IntPtr appName);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DeleteProfileFn(IntPtr session, IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetProfileFn(IntPtr session, IntPtr profile, IntPtr pInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FindProfileFn(IntPtr session, IntPtr name, IntPtr pProfile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GlobalFn(IntPtr session, IntPtr pProfile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
        static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static KpekpesNvidiaSession Open()
        {
            KpekpesNvidiaSession s = new KpekpesNvidiaSession();
            try
            {
                if (IntPtr.Size != 8)
                {
                    s.Error = "This process is 32-bit, so NVIDIA's interface cannot be used. Set Power management mode by hand.";
                    return s;
                }
                if (_query == null)
                {
                    string dll = Environment.SystemDirectory + "\\nvapi64.dll";
                    if (!System.IO.File.Exists(dll))
                    {
                        s.Error = "NVIDIA's nvapi64.dll was not found. Nothing was written. Set Power management mode by hand.";
                        return s;
                    }
                    _module = LoadLibrary(dll);
                    if (_module == IntPtr.Zero)
                    {
                        s.Error = "NVIDIA's nvapi64.dll could not be loaded. Set Power management mode by hand.";
                        return s;
                    }
                    IntPtr q = GetProcAddress(_module, "nvapi_QueryInterface");
                    if (q == IntPtr.Zero)
                    {
                        s.Error = "NVIDIA's interface entry point is missing. Set Power management mode by hand.";
                        return s;
                    }
                    _query = (QueryFn)Marshal.GetDelegateForFunctionPointer(q, typeof(QueryFn));
                }
                s._init = (StatusFn)s.Bind(IdInitialize, typeof(StatusFn));
                s._create = (SessionOutFn)s.Bind(IdCreateSession, typeof(SessionOutFn));
                s._destroy = (SessionFn)s.Bind(IdDestroySession, typeof(SessionFn));
                s._load = (SessionFn)s.Bind(IdLoadSettings, typeof(SessionFn));
                s._save = (SessionFn)s.Bind(IdSaveSettings, typeof(SessionFn));
                s._findApp = (FindAppFn)s.Bind(IdFindApp, typeof(FindAppFn));
                s._getSetting = (GetSettingFn)s.Bind(IdGetSetting, typeof(GetSettingFn));
                s._setSetting = (SetSettingFn)s.Bind(IdSetSetting, typeof(SetSettingFn));
                s._createProfile = (CreateProfileFn)s.Bind(IdCreateProfile, typeof(CreateProfileFn));
                s._createApp = (CreateAppFn)s.Bind(IdCreateApp, typeof(CreateAppFn));
                s._deleteApp = (DeleteAppFn)s.Bind(IdDeleteApp, typeof(DeleteAppFn));
                s._deleteProfile = (DeleteProfileFn)s.Bind(IdDeleteProfile, typeof(DeleteProfileFn));
                s._deleteSetting = (DeleteSettingFn)s.Bind(IdDeleteSetting, typeof(DeleteSettingFn));
                s._getProfile = (GetProfileFn)s.Bind(IdGetProfileInfo, typeof(GetProfileFn));
                s._findProfile = (FindProfileFn)s.Bind(IdFindProfile, typeof(FindProfileFn));
                s._global = (GlobalFn)s.Bind(IdGlobalProfile, typeof(GlobalFn));
                if (s._init == null || s._create == null || s._destroy == null || s._load == null || s._save == null
                    || s._findApp == null || s._getSetting == null || s._setSetting == null || s._createProfile == null
                    || s._createApp == null || s._deleteApp == null || s._deleteProfile == null || s._deleteSetting == null
                    || s._getProfile == null || s._findProfile == null || s._global == null)
                {
                    s.Error = "NVIDIA's interface is missing a function. Nothing was written. Set Power management mode by hand.";
                    return s;
                }
                if (!_initialized)
                {
                    int st = s._init();
                    s.LastStatus = st;
                    if (st != 0)
                    {
                        s.Error = Explain(st);
                        return s;
                    }
                    _initialized = true;
                }
                byte[] slot = new byte[8];
                using (Pin pin = new Pin(slot))
                {
                    int st = s._create(pin.Address);
                    s.LastStatus = st;
                    if (st != 0)
                    {
                        s.Error = Explain(st);
                        return s;
                    }
                    s._session = new IntPtr(BitConverter.ToInt64(slot, 0));
                }
                if (s._session == IntPtr.Zero)
                {
                    s.Error = "NVIDIA did not open a settings session. Nothing was written.";
                    return s;
                }
                int loaded = s._load(s._session);
                s.LastStatus = loaded;
                if (loaded != 0)
                {
                    s.Dispose();
                    s.Error = Explain(loaded);
                    return s;
                }
                s.Ok = true;
                return s;
            }
            catch (Exception ex)
            {
                s.Dispose();
                s.Error = "NVIDIA's interface could not be opened (" + ex.Message + "). Nothing was written.";
                s.Ok = false;
                return s;
            }
        }

        object Bind(uint id, Type type)
        {
            IntPtr p = _query(id);
            if (p == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(p, type);
        }

        public static string Explain(int status)
        {
            if (status == StatusIncompatible) return "NVIDIA rejected the profile data. Nothing was saved.";
            if (status == -6) return "NVIDIA's interface found no NVIDIA GPU. Nothing was written.";
            if (status == -2) return "NVIDIA's support library was not found. Set Power management mode by hand.";
            if (status == RefusePredefined) return "That profile belongs to NVIDIA. It was not deleted.";
            return "NVIDIA returned status " + status + ".";
        }

        public void Dispose()
        {
            if (_session != IntPtr.Zero && _destroy != null)
            {
                try { _destroy(_session); } catch { }
                _session = IntPtr.Zero;
            }
            Ok = false;
        }

        public int Save()
        {
            if (_session == IntPtr.Zero || _save == null) return -1;
            int st = _save(_session);
            LastStatus = st;
            return st;
        }

        public KpekpesNvidiaResult Find(string appName)
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            if (!Ready() || String.IsNullOrEmpty(appName)) { result.Status = -1; return result; }
            byte[] name = NewUnicode(appName);
            byte[] app = NewApp();
            byte[] slot = new byte[8];
            using (Pin n = new Pin(name))
            using (Pin a = new Pin(app))
            using (Pin s = new Pin(slot))
            {
                result.Status = _findApp(_session, n.Address, s.Address, a.Address);
                LastStatus = result.Status;
                if (result.Status == 0) result.Profile = BitConverter.ToInt64(slot, 0);
            }
            return result;
        }

        public KpekpesNvidiaResult FindProfile(string profileName)
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            if (!Ready() || String.IsNullOrEmpty(profileName)) { result.Status = -1; return result; }
            byte[] name = NewUnicode(profileName);
            byte[] slot = new byte[8];
            using (Pin n = new Pin(name))
            using (Pin s = new Pin(slot))
            {
                result.Status = _findProfile(_session, n.Address, s.Address);
                LastStatus = result.Status;
                if (result.Status == 0) result.Profile = BitConverter.ToInt64(slot, 0);
            }
            return result;
        }

        public KpekpesNvidiaResult GlobalProfile()
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            if (!Ready()) { result.Status = -1; return result; }
            byte[] slot = new byte[8];
            using (Pin s = new Pin(slot))
            {
                result.Status = _global(_session, s.Address);
                LastStatus = result.Status;
                if (result.Status == 0) result.Profile = BitConverter.ToInt64(slot, 0);
            }
            return result;
        }

        public KpekpesNvidiaResult ReadSetting(long profile)
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            result.Profile = profile;
            if (!Ready() || profile == 0) { result.Status = -1; return result; }
            byte[] buf = NewSetting(false, 0);
            using (Pin p = new Pin(buf))
            {
                result.Status = _getSetting(_session, new IntPtr(profile), SettingId, p.Address);
                LastStatus = result.Status;
                if (result.Status == 0)
                {
                    result.Location = unchecked((int)ReadU32(buf, 4108));
                    result.Value = unchecked((int)ReadU32(buf, 8220));
                    result.HadOwn = result.Location == 0;
                }
            }
            return result;
        }

        public KpekpesNvidiaResult ReadProfile(long profile)
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            result.Profile = profile;
            if (!Ready() || profile == 0) { result.Status = -1; return result; }
            byte[] buf = new byte[ProfileBytes];
            WriteU32(buf, 0, ProfileVersion);
            using (Pin p = new Pin(buf))
            {
                result.Status = _getProfile(_session, new IntPtr(profile), p.Address);
                LastStatus = result.Status;
                if (result.Status == 0) result.IsPredefined = unchecked((int)ReadU32(buf, 4104));
            }
            return result;
        }

        public int SetDword(long profile, int value)
        {
            if (!Ready() || profile == 0) return -1;
            byte[] buf = NewSetting(true, unchecked((uint)value));
            using (Pin p = new Pin(buf))
            {
                int st = _setSetting(_session, new IntPtr(profile), p.Address);
                LastStatus = st;
                return st;
            }
        }

        public int DeleteSetting(long profile)
        {
            if (!Ready() || profile == 0) return -1;
            int st = _deleteSetting(_session, new IntPtr(profile), SettingId);
            LastStatus = st;
            return st;
        }

        public int DeleteOwnedProfile(long profile)
        {
            KpekpesNvidiaResult info = ReadProfile(profile);
            if (info.Status != 0) return info.Status;
            if (info.IsPredefined != 0) return RefusePredefined;
            if (!Ready()) return -1;
            int st = _deleteProfile(_session, new IntPtr(profile));
            LastStatus = st;
            return st;
        }

        public KpekpesNvidiaResult CreateProfile(string profileName)
        {
            KpekpesNvidiaResult result = new KpekpesNvidiaResult();
            if (!Ready() || String.IsNullOrEmpty(profileName)) { result.Status = -1; return result; }
            byte[] info = new byte[ProfileBytes];
            WriteU32(info, 0, ProfileVersion);
            CopyUtf16(info, 4, UnicodeBytes, profileName);
            byte[] slot = new byte[8];
            using (Pin i = new Pin(info))
            using (Pin s = new Pin(slot))
            {
                result.Status = _createProfile(_session, i.Address, s.Address);
                LastStatus = result.Status;
                if (result.Status == 0) result.Profile = BitConverter.ToInt64(slot, 0);
            }
            return result;
        }

        public int CreateApplication(long profile, string appName)
        {
            if (!Ready() || profile == 0 || String.IsNullOrEmpty(appName)) return -1;
            byte[] app = NewApp();
            CopyUtf16(app, 8, UnicodeBytes, appName);
            using (Pin a = new Pin(app))
            {
                int st = _createApp(_session, new IntPtr(profile), a.Address);
                LastStatus = st;
                return st;
            }
        }

        public int DeleteApplication(long profile, string appName)
        {
            if (!Ready() || profile == 0 || String.IsNullOrEmpty(appName)) return -1;
            byte[] name = NewUnicode(appName);
            using (Pin n = new Pin(name))
            {
                int st = _deleteApp(_session, new IntPtr(profile), n.Address);
                LastStatus = st;
                return st;
            }
        }

        bool Ready()
        {
            return Ok && _session != IntPtr.Zero;
        }

        static byte[] NewUnicode(string text)
        {
            byte[] buf = new byte[UnicodeBytes];
            CopyUtf16(buf, 0, UnicodeBytes, text);
            return buf;
        }

        static byte[] NewApp()
        {
            byte[] buf = new byte[AppBytes];
            WriteU32(buf, 0, AppVersion);
            return buf;
        }

        static byte[] NewSetting(bool withValue, uint value)
        {
            byte[] buf = new byte[SettingBytes];
            WriteU32(buf, 0, SettingVersion);
            CopyUtf16(buf, 4, UnicodeBytes, "Power management mode");
            WriteU32(buf, 4100, SettingId);
            if (withValue)
            {
                WriteU32(buf, 4104, 0);
                WriteU32(buf, 8220, value);
            }
            return buf;
        }

        static void CopyUtf16(byte[] buf, int offset, int fieldBytes, string text)
        {
            if (text == null) text = "";
            byte[] raw = Encoding.Unicode.GetBytes(text);
            int n = raw.Length;
            if (n > fieldBytes - 2) n = fieldBytes - 2;
            if (n > 0) Buffer.BlockCopy(raw, 0, buf, offset, n);
        }

        static void WriteU32(byte[] buf, int offset, uint value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            Buffer.BlockCopy(raw, 0, buf, offset, 4);
        }

        static uint ReadU32(byte[] buf, int offset)
        {
            return BitConverter.ToUInt32(buf, offset);
        }

        sealed class Pin : IDisposable
        {
            GCHandle _handle;
            public IntPtr Address;
            public Pin(byte[] buffer)
            {
                _handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                Address = _handle.AddrOfPinnedObject();
            }
            public void Dispose()
            {
                if (_handle.IsAllocated) _handle.Free();
            }
        }
    }
}
