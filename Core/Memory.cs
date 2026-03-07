using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FemboyChanger.Core
{
    public class Memory
    {
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

        private IntPtr _processHandle;
        public Process Process { get; private set; }
        public IntPtr ClientDll { get; private set; }
        public int ClientDllSize { get; private set; }
        public IntPtr Engine2Dll { get; private set; }

        public bool Attach(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length > 0)
            {
                Process = processes[0];
                _processHandle = OpenProcess(0x001F0FFF, false, Process.Id); // PROCESS_ALL_ACCESS

                foreach (ProcessModule module in Process.Modules)
                {
                    if (module.ModuleName == "client.dll")
                    {
                        ClientDll = module.BaseAddress;
                        ClientDllSize = module.ModuleMemorySize;
                    }
                    else if (module.ModuleName == "engine2.dll")
                        Engine2Dll = module.BaseAddress;
                }

                return _processHandle != IntPtr.Zero && ClientDll != IntPtr.Zero;
            }
            return false;
        }

        public IntPtr Allocate(int size)
        {
            return VirtualAllocEx(_processHandle, IntPtr.Zero, (uint)size, 0x1000 | 0x2000, 0x40); // MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE
        }

        public void Free(IntPtr address)
        {
            VirtualFreeEx(_processHandle, address, 0, 0x8000); // MEM_RELEASE
        }

        public void CallThread(nint funcAddress, nint arg = 0)
        {
            if (funcAddress == 0) return;
            IntPtr hThread = CreateRemoteThread(_processHandle, IntPtr.Zero, 0, funcAddress, arg, 0, out _);
            if (hThread != IntPtr.Zero)
            {
                WaitForSingleObject(hThread, 0xFFFFFFFF); // INFINITE
                CloseHandle(hThread);
            }
        }

        public nint SigScan(string pattern)
        {
            byte[] moduleBytes = new byte[ClientDllSize];
            ReadProcessMemory(_processHandle, ClientDll, moduleBytes, ClientDllSize, out _);

            string[] patternParts = pattern.Split(' ');
            for (int i = 0; i < moduleBytes.Length - patternParts.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < patternParts.Length; j++)
                {
                    if (patternParts[j] == "?" || patternParts[j] == "??") continue;
                    if (moduleBytes[i + j] != Convert.ToByte(patternParts[j], 16))
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return ClientDll + i;
            }
            return 0;
        }

        public T Read<T>(IntPtr address) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];
            if (ReadProcessMemory(_processHandle, address, buffer, size, out _))
            {
                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                T result = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
                handle.Free();
                return result;
            }
            return default;
        }

        public void Write<T>(IntPtr address, T value) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(value, ptr, true);
            Marshal.Copy(ptr, buffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            WriteProcessMemory(_processHandle, address, buffer, size, out _);
        }
        
        public string ReadString(IntPtr address, int length = 256)
        {
            byte[] buffer = new byte[length];
            if (ReadProcessMemory(_processHandle, address, buffer, length, out _))
            {
                int nullCharIndex = Array.IndexOf(buffer, (byte)0);
                if (nullCharIndex >= 0)
                    return Encoding.UTF8.GetString(buffer, 0, nullCharIndex);
                return Encoding.UTF8.GetString(buffer);
            }
            return string.Empty;
        }

        public void Dispose()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
        }
    }
}