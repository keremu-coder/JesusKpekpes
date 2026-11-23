// Shader cache size and V-Sync for the NVIDIA GLOBAL profile, through NvAPI only.
// This is a separate class from NvidiaPower.cs on purpose: if anything here fails to compile or run,
// the Call of Duty power-mode code is not affected.
// Ids and values are from NVIDIA's public NvApiDriverSettings.h (github.com/NVIDIA/nvapi):
//   PS_SHADERDISKCACHE_ID          0x00198FFF   0 off, 1 on
//   PS_SHADERDISKCACHE_MAX_SIZE_ID 0x00AC8497   0xFFFFFFFF is the largest value (Unlimited in the control panel)
//   VSYNCMODE_ID                   0x00A879CF   0x08416747 force off
// Buffer layout is the same as NvidiaPower.cs (setting 12320 bytes, version 77856, location at 4108, value at 8220).
// The driver database file is never opened. C# 5 syntax only, because Windows PowerShell compiles it.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Kpekpes
{
    public sealed class KpekpesGlobalResult
    {
        public int Status;
        public long Profile;
        public int Location;
        public int Value;
    }

    public sealed class KpekpesNvidiaGlobal : IDisposable
    {
        public const uint ShaderCacheId = 0x00198FFF;
        public const uint ShaderCacheSizeId = 0x00AC8497;
        public const uint VsyncId = 0x00A879CF;
        public const int ShaderCacheOn = 1;
        public const int ShaderCacheUnlimited = -1;   // 0xFFFFFFFF
        public const int VsyncForceOff = 0x08416747;

        const int SettingBytes = 12320;
        const int SettingVersion = 77856;
        const int UnicodeBytes = 4096;
        const uint IdInitialize = 0x0150E828;
        const uint IdCreateSession = 0x0694D52E;
        const uint IdDestroySession = 0xDAD9CFF8;
        const uint IdLoadSettings = 0x375DBD6B;
        const uint IdSaveSettings = 0xFCBC7E14;
        const uint IdGetSetting = 0x73BF8338;
        const uint IdSetSetting = 0x577DD202;
        const uint IdDeleteSetting = 0xE4A26362;
        const uint IdGlobalProfile = 0x617BFF9F;

        static IntPtr _module;
        static QueryFn _query;

        IntPtr _session;
        long _global;
        StatusFn _init;
        SessionOutFn _create;
        SessionFn _destroy;
        SessionFn _load;
        SessionFn _save;
        GetSettingFn _getSetting;
        SetSettingFn _setSetting;
        DeleteSettingFn _deleteSetting;
        GlobalFn _globalProfile;

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
        delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr pSetting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GlobalFn(IntPtr session, IntPtr pProfile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
        static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static KpekpesNvidiaGlobal Open()
        {
            KpekpesNvidiaGlobal s = new KpekpesNvidiaGlobal();
            try
            {
                if (IntPtr.Size != 8)
                {
                    s.Error = "This process is 32-bit, so NVIDIA's interface cannot be used.";
                    return s;
                }
                if (_query == null)
                {
                    string dll = Environment.SystemDirectory + "\\nvapi64.dll";
                    if (!System.IO.File.Exists(dll))
                    {
                        s.Error = "NVIDIA's nvapi64.dll was not found. Nothing was written.";
                        return s;
                    }
                    _module = LoadLibrary(dll);
                    if (_module == IntPtr.Zero)
                    {
                        s.Error = "NVIDIA's nvapi64.dll could not be loaded. Nothing was written.";
                        return s;
                    }
                    IntPtr q = GetProcAddress(_module, "nvapi_QueryInterface");
                    if (q == IntPtr.Zero)
                    {
                        s.Error = "NVIDIA's interface entry point is missing. Nothing was written.";
                        return s;
                    }
                    _query = (QueryFn)Marshal.GetDelegateForFunctionPointer(q, typeof(QueryFn));
                }
                s._init = (StatusFn)s.Bind(IdInitialize, typeof(StatusFn));
                s._create = (SessionOutFn)s.Bind(IdCreateSession, typeof(SessionOutFn));
                s._destroy = (SessionFn)s.Bind(IdDestroySession, typeof(SessionFn));
                s._load = (SessionFn)s.Bind(IdLoadSettings, typeof(SessionFn));
                s._save = (SessionFn)s.Bind(IdSaveSettings, typeof(SessionFn));
                s._getSetting = (GetSettingFn)s.Bind(IdGetSetting, typeof(GetSettingFn));
                s._setSetting = (SetSettingFn)s.Bind(IdSetSetting, typeof(SetSettingFn));
                s._deleteSetting = (DeleteSettingFn)s.Bind(IdDeleteSetting, typeof(DeleteSettingFn));
                s._globalProfile = (GlobalFn)s.Bind(IdGlobalProfile, typeof(GlobalFn));
                if (s._init == null || s._create == null || s._destroy == null || s._load == null || s._save == null
                    || s._getSetting == null || s._setSetting == null || s._deleteSetting == null || s._globalProfile == null)
                {
                    s.Error = "NVIDIA's interface is missing a function. Nothing was written.";
                    return s;
                }
                int st = s._init();
                s.LastStatus = st;
                if (st != 0)
                {
                    s.Error = "NVIDIA returned status " + st + ".";
                    return s;
                }
                byte[] slot = new byte[8];
                using (Pin pin = new Pin(slot))
                {
                    st = s._create(pin.Address);
                    s.LastStatus = st;
                    if (st != 0)
                    {
                        s.Error = "NVIDIA returned status " + st + ".";
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
                    s.Error = "NVIDIA returned status " + loaded + ".";
                    return s;
                }
                byte[] gslot = new byte[8];
                using (Pin gp = new Pin(gslot))
                {
                    int gs = s._globalProfile(s._session, gp.Address);
                    s.LastStatus = gs;
                    if (gs != 0)
                    {
                        s.Dispose();
                        s.Error = "NVIDIA's global profile could not be read (status " + gs + "). Nothing was written.";
                        return s;
                    }
                    s._global = BitConverter.ToInt64(gslot, 0);
                }
                if (s._global == 0)
                {
                    s.Dispose();
                    s.Error = "NVIDIA's global profile was empty. Nothing was written.";
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

        public void Dispose()
        {
            if (_session != IntPtr.Zero && _destroy != null)
            {
                try { _destroy(_session); } catch { }
                _session = IntPtr.Zero;
            }
            Ok = false;
        }

        public KpekpesGlobalResult Read(uint settingId)
        {
            KpekpesGlobalResult result = new KpekpesGlobalResult();
            if (!Ok || _session == IntPtr.Zero) { result.Status = -1; return result; }
            byte[] buf = NewSetting(settingId, false, 0);
            using (Pin p = new Pin(buf))
            {
                result.Status = _getSetting(_session, new IntPtr(_global), settingId, p.Address);
                LastStatus = result.Status;
                if (result.Status == 0)
                {
                    result.Location = unchecked((int)ReadU32(buf, 4108));
                    result.Value = unchecked((int)ReadU32(buf, 8220));
                }
            }
            return result;
        }

        public int Set(uint settingId, int value)
        {
            if (!Ok || _session == IntPtr.Zero) return -1;
            byte[] buf = NewSetting(settingId, true, unchecked((uint)value));
            using (Pin p = new Pin(buf))
            {
                int st = _setSetting(_session, new IntPtr(_global), p.Address);
                LastStatus = st;
                return st;
            }
        }

        public int Delete(uint settingId)
        {
            if (!Ok || _session == IntPtr.Zero) return -1;
            int st = _deleteSetting(_session, new IntPtr(_global), settingId);
            LastStatus = st;
            return st;
        }

        public int Save()
        {
            if (!Ok || _session == IntPtr.Zero || _save == null) return -1;
            int st = _save(_session);
            LastStatus = st;
            return st;
        }

        static byte[] NewSetting(uint settingId, bool withValue, uint value)
        {
            byte[] buf = new byte[SettingBytes];
            WriteU32(buf, 0, SettingVersion);
            byte[] raw = Encoding.Unicode.GetBytes("Kpekpes setting");
            int n = raw.Length;
            if (n > UnicodeBytes - 2) n = UnicodeBytes - 2;
            Buffer.BlockCopy(raw, 0, buf, 4, n);
            WriteU32(buf, 4100, settingId);
            if (withValue)
            {
                WriteU32(buf, 4104, 0);
                WriteU32(buf, 8220, value);
            }
            return buf;
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
