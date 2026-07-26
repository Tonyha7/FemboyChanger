using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FemboyChanger.Core
{
    public class Memory : IDisposable
    {
        private const int PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        private IntPtr _processHandle;
        private byte[]? _clientImage;

        public Process? Process { get; private set; }
        public IntPtr ClientDll { get; private set; }
        public int ClientDllSize { get; private set; }
        public IntPtr Engine2Dll { get; private set; }

        public bool IsAttached => _processHandle != IntPtr.Zero && ClientDll != IntPtr.Zero;

        /// <summary>
        /// Attaches to <paramref name="processName"/>. Cheap and idempotent: an existing, still
        /// valid handle is reused instead of re-opening the process (the previous version opened a
        /// fresh handle on every tick and never closed it, leaking hundreds of handles per second
        /// and re-walking the module list each time).
        /// </summary>
        public bool Attach(string processName)
        {
            if (IsAttached && Process != null)
            {
                try
                {
                    if (!Process.HasExited) return true;
                }
                catch { /* fall through and re-attach */ }
                Detach();
            }

            Process[] processes = System.Diagnostics.Process.GetProcessesByName(processName);
            if (processes.Length == 0) return false;

            Process target = processes[0];
            IntPtr handle = OpenProcess(PROCESS_ALL_ACCESS, false, target.Id);
            if (handle == IntPtr.Zero)
            {
                SkinChangerLogic.Log($"[Memory] OpenProcess({processName}, pid {target.Id}) failed, win32 error {Marshal.GetLastWin32Error()}. Run as administrator?");
                return false;
            }

            IntPtr client = IntPtr.Zero, engine = IntPtr.Zero;
            int clientSize = 0;
            try
            {
                foreach (ProcessModule module in target.Modules)
                {
                    if (module.ModuleName == "client.dll")
                    {
                        client = module.BaseAddress;
                        clientSize = module.ModuleMemorySize;
                    }
                    else if (module.ModuleName == "engine2.dll")
                    {
                        engine = module.BaseAddress;
                    }
                }
            }
            catch (Exception ex)
            {
                SkinChangerLogic.Log($"[Memory] Enumerating modules failed: {ex.Message}");
            }

            if (client == IntPtr.Zero)
            {
                // cs2.exe is up but client.dll is not mapped yet (still on the launcher/loading screen).
                CloseHandle(handle);
                return false;
            }

            _processHandle = handle;
            Process = target;
            ClientDll = client;
            ClientDllSize = clientSize;
            Engine2Dll = engine;
            _clientImage = null;

            SkinChangerLogic.Log($"[Memory] Attached to {processName} pid={target.Id} client.dll=0x{client.ToInt64():X} size=0x{clientSize:X}");
            return true;
        }

        public void Detach()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            Process = null;
            ClientDll = IntPtr.Zero;
            ClientDllSize = 0;
            Engine2Dll = IntPtr.Zero;
            _clientImage = null;
        }

        public IntPtr Allocate(int size)
        {
            return VirtualAllocEx(_processHandle, IntPtr.Zero, (uint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        }

        public void Free(IntPtr address)
        {
            if (address != IntPtr.Zero) VirtualFreeEx(_processHandle, address, 0, MEM_RELEASE);
        }

        public void CallThread(nint funcAddress, nint arg = 0)
        {
            if (funcAddress == 0 || _processHandle == IntPtr.Zero) return;
            IntPtr hThread = CreateRemoteThread(_processHandle, IntPtr.Zero, 0, funcAddress, arg, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                SkinChangerLogic.Log($"[Memory] CreateRemoteThread(0x{funcAddress:X}) failed, win32 error {Marshal.GetLastWin32Error()}");
                return;
            }
            WaitForSingleObject(hThread, 5000);
            CloseHandle(hThread);
        }

        /// <summary>Writes into a code page, temporarily making it writable.</summary>
        public bool WriteProtected(nint address, byte[] data)
        {
            if (_processHandle == IntPtr.Zero) return false;
            if (!VirtualProtectEx(_processHandle, address, (UIntPtr)(uint)data.Length, PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                SkinChangerLogic.Log($"[Memory] VirtualProtectEx(0x{address:X}) failed, win32 error {Marshal.GetLastWin32Error()}");
                return false;
            }
            bool ok = WriteProcessMemory(_processHandle, address, data, data.Length, out _);
            VirtualProtectEx(_processHandle, address, (UIntPtr)(uint)data.Length, oldProtect, out _);
            if (!ok)
                SkinChangerLogic.Log($"[Memory] WriteProcessMemory(0x{address:X}) failed, win32 error {Marshal.GetLastWin32Error()}");
            return ok;
        }

        /// <summary>
        /// IDA-style signature scan over client.dll. The module image is snapshotted once per
        /// attach and the pattern is pre-parsed, so this no longer converts hex strings inside the
        /// innermost comparison loop.
        /// </summary>
        public nint SigScan(string pattern)
        {
            List<nint> hits = ScanAll(pattern, 1);
            return hits.Count > 0 ? hits[0] : 0;
        }

        /// <summary>All matches of an IDA-style byte pattern inside client.dll.</summary>
        public List<nint> ScanAll(string pattern, int limit = 64)
        {
            var results = new List<nint>();
            byte[]? image = EnsureImage();
            if (image == null) return results;

            string[] parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            var mask = new bool[parts.Length]; // true = compare
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "?" || parts[i] == "??") continue;
                bytes[i] = Convert.ToByte(parts[i], 16);
                mask[i] = true;
            }

            int last = image.Length - parts.Length;
            for (int i = 0; i <= last; i++)
            {
                int j = 0;
                for (; j < parts.Length; j++)
                {
                    if (mask[j] && image[i + j] != bytes[j]) break;
                }
                if (j != parts.Length) continue;
                results.Add(ClientDll + i);
                if (results.Count >= limit) break;
            }
            return results;
        }

        /// <summary>Finds a NUL-terminated ASCII string in client.dll.</summary>
        public nint FindAsciiString(string text)
        {
            var pattern = new StringBuilder();
            foreach (byte b in Encoding.ASCII.GetBytes(text)) pattern.Append(b.ToString("X2")).Append(' ');
            pattern.Append("00");
            return SigScan(pattern.ToString());
        }

        /// <summary>
        /// Resolves the target of a RIP-relative instruction: <paramref name="address"/> is the
        /// start of the instruction, <paramref name="dispOffset"/> where its 4-byte displacement
        /// begins and <paramref name="length"/> the whole instruction's length.
        /// </summary>
        public nint ResolveRip(nint address, int dispOffset, int length)
        {
            byte[]? image = EnsureImage();
            if (image == null) return 0;
            long index = address - ClientDll;
            if (index < 0 || index + length > image.Length) return 0;
            int disp = BitConverter.ToInt32(image, (int)index + dispOffset);
            return address + length + disp;
        }

        private byte[]? EnsureImage()
        {
            if (!IsAttached || ClientDllSize <= 0) return null;
            if (_clientImage != null) return _clientImage;

            var buffer = new byte[ClientDllSize];
            if (!ReadProcessMemory(_processHandle, ClientDll, buffer, ClientDllSize, out IntPtr read) || (long)read == 0)
            {
                SkinChangerLogic.Log($"[Memory] Failed to snapshot client.dll for scanning, win32 error {Marshal.GetLastWin32Error()}");
                return null;
            }
            _clientImage = buffer;
            return _clientImage;
        }

        /// <summary>Drops the cached client.dll image once signature scanning is done (~37 MB).</summary>
        public void ReleaseScanCache() => _clientImage = null;

        public T Read<T>(IntPtr address) where T : unmanaged
        {
            Read(address, out T value);
            return value;
        }

        public bool Read<T>(IntPtr address, out T value) where T : unmanaged
        {
            value = default;
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero) return false;

            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];
            if (!ReadProcessMemory(_processHandle, address, buffer, size, out IntPtr read) || (long)read != size)
                return false;

            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { value = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
            return true;
        }

        public byte[]? ReadBytes(IntPtr address, int length)
        {
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero || length <= 0) return null;
            byte[] buffer = new byte[length];
            if (!ReadProcessMemory(_processHandle, address, buffer, length, out IntPtr read) || (long)read != length)
                return null;
            return buffer;
        }

        public bool Write<T>(IntPtr address, T value) where T : unmanaged
        {
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero) return false;

            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                // AllocHGlobal hands back uninitialised heap memory, and StructureToPtr only
                // fills declared fields - without this, explicit-layout padding (CEconItemAttribute
                // has 0x26 bytes of it) would be written into the game as leftover heap garbage.
                Marshal.Copy(buffer, 0, ptr, size);
                Marshal.StructureToPtr(value, ptr, false);
                Marshal.Copy(ptr, buffer, 0, size);
            }
            finally { Marshal.FreeHGlobal(ptr); }

            return WriteProcessMemory(_processHandle, address, buffer, size, out _);
        }

        public bool WriteBytes(IntPtr address, byte[] data)
        {
            if (_processHandle == IntPtr.Zero || address == IntPtr.Zero) return false;
            return WriteProcessMemory(_processHandle, address, data, data.Length, out _);
        }

        public string ReadString(IntPtr address, int length = 256)
        {
            byte[]? buffer = ReadBytes(address, length);
            if (buffer == null) return string.Empty;
            int nul = Array.IndexOf(buffer, (byte)0);
            return nul >= 0 ? Encoding.UTF8.GetString(buffer, 0, nul) : Encoding.UTF8.GetString(buffer);
        }

        public void Dispose() => Detach();
    }
}
