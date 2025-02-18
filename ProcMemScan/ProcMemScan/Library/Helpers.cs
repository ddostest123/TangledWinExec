﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ProcMemScan.Interop;

namespace ProcMemScan.Library
{
    using NTSTATUS = Int32;
    using SIZE_T = UIntPtr;

    internal class Helpers
    {
        public static string ConvertDriveLetterToDeviceName(string driveLetter)
        {
            int nReturnedPathLength;
            var devicePathName = new StringBuilder((int)Win32Consts.MAX_PATH);

            if (string.IsNullOrEmpty(driveLetter))
                return null;

            nReturnedPathLength = NativeMethods.QueryDosDevice(
                driveLetter,
                devicePathName,
                devicePathName.Capacity);

            if (nReturnedPathLength == 0)
                return null;
            else
                return devicePathName.ToString();
        }


        public static string ConvertLargeIntegerToLocalTimeString(LARGE_INTEGER fileTime)
        {
            if (NativeMethods.FileTimeToSystemTime(in fileTime, out SYSTEMTIME systemTime))
            {
                if (NativeMethods.SystemTimeToTzSpecificLocalTime(
                    IntPtr.Zero,
                    in systemTime,
                    out SYSTEMTIME localTime))
                {
                    return string.Format(
                        "{0}/{1}/{2} {3}:{4}:{5}",
                        localTime.wYear.ToString("D4"),
                        localTime.wMonth.ToString("D2"),
                        localTime.wDay.ToString("D2"),
                        localTime.wHour.ToString("D2"),
                        localTime.wMinute.ToString("D2"),
                        localTime.wSecond.ToString("D2"));
                }
                else
                {
                    return string.Format(
                        "{0}/{1}/{2} {3}:{4}:{5}",
                        systemTime.wYear.ToString("D4"),
                        systemTime.wMonth.ToString("D2"),
                        systemTime.wDay.ToString("D2"),
                        systemTime.wHour.ToString("D2"),
                        systemTime.wMinute.ToString("D2"),
                        systemTime.wSecond.ToString("D2"));
                }
            }
            else
            {
                return "N/A";
            }
        }


        public static IntPtr CreateExportFile(string path)
        {
            NTSTATUS ntstatus;
            var objectAttributes = new OBJECT_ATTRIBUTES(
                string.Format(@"\??\{0}", Path.GetFullPath(path)),
                OBJECT_ATTRIBUTES_FLAGS.OBJ_CASE_INSENSITIVE);
            var pIoStatusBlock = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IO_STATUS_BLOCK)));

            ntstatus = NativeMethods.NtCreateFile(
                out IntPtr hFile,
                ACCESS_MASK.FILE_GENERIC_READ | ACCESS_MASK.FILE_GENERIC_WRITE,
                in objectAttributes,
                pIoStatusBlock,
                IntPtr.Zero,
                FILE_ATTRIBUTE_FLAGS.NORMAL,
                FILE_SHARE_ACCESS.NONE,
                FILE_CREATE_DISPOSITION.OPEN_IF,
                FILE_CREATE_OPTIONS.RANDOM_ACCESS | FILE_CREATE_OPTIONS.NON_DIRECTORY_FILE | FILE_CREATE_OPTIONS.SYNCHRONOUS_IO_NONALERT,
                IntPtr.Zero,
                0);
            Marshal.FreeHGlobal(pIoStatusBlock);

            if (ntstatus != Win32Consts.STATUS_SUCCESS)
                return Win32Consts.INVALID_HANDLE_VALUE;
            else
                return hFile;
        }


        public static List<string> EnumEnvrionments(
            IntPtr hProcess,
            RTL_USER_PROCESS_PARAMETERS processParameters)
        {
            string unicodeString;
            IntPtr pUnicodeString;
            int cursor = 0;
            var results = new List<string>();

            if (processParameters.Environment == IntPtr.Zero)
                return results;

            IntPtr pBufferToRead = ReadMemory(
                hProcess,
                processParameters.Environment,
                (uint)processParameters.EnvironmentSize);

            if (pBufferToRead == IntPtr.Zero)
                return results;

            do
            {
                pUnicodeString = new IntPtr(pBufferToRead.ToInt64() + cursor);
                unicodeString = Marshal.PtrToStringUni(pUnicodeString);

                results.Add(unicodeString);

                cursor += (unicodeString.Length * 2);
                cursor += 2;
            } while ((uint)cursor < (uint)processParameters.EnvironmentSize);

            Marshal.FreeHGlobal(pBufferToRead);

            return results;
        }


        public static List<MEMORY_BASIC_INFORMATION> EnumMemoryBasicInformation(IntPtr hProcess)
        {
            NTSTATUS ntstatus;
            bool status;
            MEMORY_BASIC_INFORMATION memoryBasicInfo;
            var results = new List<MEMORY_BASIC_INFORMATION>();
            int nInfoBufferSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            IntPtr pInfoBuffer = Marshal.AllocHGlobal(nInfoBufferSize);
            IntPtr pCurrentBaseAddress = IntPtr.Zero;

            do
            {
                ntstatus = NativeMethods.NtQueryVirtualMemory(
                    hProcess,
                    pCurrentBaseAddress,
                    MEMORY_INFORMATION_CLASS.MemoryBasicInformation,
                    pInfoBuffer,
                    new SIZE_T((uint)nInfoBufferSize),
                    IntPtr.Zero);
                status = (ntstatus == Win32Consts.STATUS_SUCCESS);

                if (status)
                {
                    memoryBasicInfo = (MEMORY_BASIC_INFORMATION)Marshal.PtrToStructure(
                        pInfoBuffer,
                        typeof(MEMORY_BASIC_INFORMATION));

                    results.Add(memoryBasicInfo);

                    pCurrentBaseAddress = new IntPtr(
                        pCurrentBaseAddress.ToInt64() + (long)memoryBasicInfo.RegionSize.ToUInt64());
                }
            } while (status);

            Marshal.FreeHGlobal(pInfoBuffer);

            return results;
        }


        public static Dictionary<string, IntPtr>EnumModules(
            IntPtr hProcess,
            IntPtr pPeb)
        {
            int nOffsetLdr;
            int nOffsetInMemoryOrderModuleList;
            int nOffsetInMemoryOrderLinks;
            int nSizeTableEntry;
            string modulePathName;
            IntPtr pTempBuffer;
            IntPtr pLdr;
            IntPtr pMemoryOrderModuleList;
            bool is32bit;
            LDR_DATA_TABLE_ENTRY tableEntry;
            var modules = new Dictionary<string, IntPtr>();

            if (Environment.Is64BitOperatingSystem)
            {
                NativeMethods.IsWow64Process(hProcess, out is32bit);

                if (Environment.Is64BitProcess && is32bit)
                {
                    Console.WriteLine("[-] To 32bit process, should be built as 32bit binary.");

                    return modules;
                }
            }
            else
            {
                is32bit = true;
            }

            if (is32bit)
                nOffsetLdr = Marshal.OffsetOf(typeof(PEB32_PARTIAL), "Ldr").ToInt32();
            else
                nOffsetLdr = Marshal.OffsetOf(typeof(PEB64_PARTIAL), "Ldr").ToInt32();

            nOffsetInMemoryOrderModuleList = Marshal.OffsetOf(
                typeof(PEB_LDR_DATA),
                "InMemoryOrderModuleList").ToInt32();
            nOffsetInMemoryOrderLinks = Marshal.OffsetOf(
                typeof(LDR_DATA_TABLE_ENTRY),
                "InMemoryOrderLinks").ToInt32();
            nSizeTableEntry = Marshal.SizeOf(typeof(LDR_DATA_TABLE_ENTRY));

            pTempBuffer = ReadMemory(
                hProcess,
                new IntPtr(pPeb.ToInt64() + nOffsetLdr),
                8);

            if (pTempBuffer == IntPtr.Zero)
                return modules;

            pLdr = Marshal.ReadIntPtr(pTempBuffer);
            Marshal.FreeHGlobal(pTempBuffer);

            pTempBuffer = ReadMemory(
                hProcess,
                new IntPtr(pLdr.ToInt64() + nOffsetInMemoryOrderModuleList),
                (uint)IntPtr.Size);

            if (pTempBuffer == IntPtr.Zero)
                return modules;

            pMemoryOrderModuleList = new IntPtr(
                Marshal.ReadIntPtr(pTempBuffer).ToInt64() - nOffsetInMemoryOrderLinks);
            Marshal.FreeHGlobal(pTempBuffer);

            do
            {
                pTempBuffer = ReadMemory(
                    hProcess,
                    pMemoryOrderModuleList,
                    (uint)nSizeTableEntry);

                if (pTempBuffer == IntPtr.Zero)
                    break;

                tableEntry = (LDR_DATA_TABLE_ENTRY)Marshal.PtrToStructure(
                    pTempBuffer,
                    typeof(LDR_DATA_TABLE_ENTRY));
                Marshal.FreeHGlobal(pTempBuffer);

                pTempBuffer = ReadMemory(
                    hProcess,
                    tableEntry.FullDllName.GetBuffer(),
                    (uint)tableEntry.FullDllName.MaximumLength);

                if (pTempBuffer == IntPtr.Zero)
                    break;

                modulePathName = Marshal.PtrToStringUni(pTempBuffer);
                Marshal.FreeHGlobal(pTempBuffer);

                if (modules.ContainsKey(modulePathName))
                    break;
                else if (tableEntry.DllBase != IntPtr.Zero)
                    modules.Add(modulePathName, tableEntry.DllBase);

                pTempBuffer = ReadMemory(
                    hProcess,
                    new IntPtr(pMemoryOrderModuleList.ToInt64() + nOffsetInMemoryOrderLinks),
                    (uint)IntPtr.Size);

                if (pTempBuffer == IntPtr.Zero)
                    break;

                pMemoryOrderModuleList = new IntPtr(
                    Marshal.ReadIntPtr(pTempBuffer).ToInt64() - nOffsetInMemoryOrderLinks);
                Marshal.FreeHGlobal(pTempBuffer);
            } while (true);

            return modules;
        }


        public static Dictionary<string, string> EnumVolumePathNameAlias()
        {
            int error;
            int nReturnedLength;
            IntPtr pOutBuffer;
            IntPtr pVolumePathName;
            string volumePathName;
            int nSizeBuffer = 0x1000;
            int cursor = 0;
            var devicePathName = new StringBuilder();
            var results = new Dictionary<string, string>();

            do
            {
                pOutBuffer = Marshal.AllocHGlobal(nSizeBuffer);

                nReturnedLength = NativeMethods.QueryDosDevice(
                    null,
                    pOutBuffer,
                    nSizeBuffer);
                error = Marshal.GetLastWin32Error();

                if (nReturnedLength == 0)
                {
                    Marshal.FreeHGlobal(pOutBuffer);
                    nSizeBuffer += 0x1000;
                }
            } while (nReturnedLength == 0 && error == Win32Consts.ERROR_INSUFFICIENT_BUFFER);

            if (nReturnedLength == 0)
                return results;

            pVolumePathName = pOutBuffer;

            do
            {
                devicePathName.Capacity = (int)Win32Consts.MAX_PATH;
                volumePathName = Marshal.PtrToStringAnsi(pVolumePathName);

                if (string.IsNullOrEmpty(volumePathName))
                    break;

                NativeMethods.QueryDosDevice(
                    volumePathName,
                    devicePathName,
                    devicePathName.Capacity);

                results.Add(volumePathName, devicePathName.ToString());

                cursor += volumePathName.Length;
                cursor++;

                pVolumePathName = new IntPtr(pOutBuffer.ToInt64() + cursor);
                devicePathName.Clear();
            } while (cursor < nReturnedLength);

            Marshal.FreeHGlobal(pOutBuffer);

            return results;
        }


        public static int GetArchitectureBitness(IMAGE_FILE_MACHINE arch)
        {
            if (arch == IMAGE_FILE_MACHINE.X86)
                return 32;
            else if (arch == IMAGE_FILE_MACHINE.ARM)
                return 32;
            else if (arch == IMAGE_FILE_MACHINE.ARM2)
                return 32;
            else if (arch == IMAGE_FILE_MACHINE.IA64)
                return 64;
            else if (arch == IMAGE_FILE_MACHINE.AMD64)
                return 64;
            else if (arch == IMAGE_FILE_MACHINE.ARM64)
                return 64;
            else
                return 0;
        }


        public static IntPtr GetImageBaseAddress(
            IntPtr hProcess,
            IntPtr pPeb)
        {
            IntPtr pImageBase;
            IntPtr pReadBuffer;
            int nSizePointer;
            int nOffsetImageBaseAddress;

            if (Environment.Is64BitOperatingSystem)
            {
                if (!NativeMethods.IsWow64Process(
                    hProcess,
                    out bool Wow64Process))
                {
                    return IntPtr.Zero;
                }

                if (Wow64Process)
                {
                    nSizePointer = 4;
                    nOffsetImageBaseAddress = Marshal.OffsetOf(
                        typeof(PEB32_PARTIAL),
                        "ImageBaseAddress").ToInt32();
                }
                else
                {
                    nSizePointer = 8;
                    nOffsetImageBaseAddress = Marshal.OffsetOf(
                        typeof(PEB64_PARTIAL),
                        "ImageBaseAddress").ToInt32();
                }
            }
            else
            {
                nSizePointer = 4;
                nOffsetImageBaseAddress = Marshal.OffsetOf(
                    typeof(PEB32_PARTIAL),
                    "ImageBaseAddress").ToInt32();
            }

            pReadBuffer = ReadMemory(
                hProcess,
                new IntPtr(pPeb.ToInt64() + nOffsetImageBaseAddress),
                (uint)nSizePointer);

            if (pReadBuffer == IntPtr.Zero)
                return IntPtr.Zero;

            if (nSizePointer == 4)
            {
                pImageBase = new IntPtr(Marshal.ReadInt32(pReadBuffer));
            }
            else
            {
                pImageBase = new IntPtr(Marshal.ReadInt64(pReadBuffer));
            }

            Marshal.FreeHGlobal(pReadBuffer);

            return pImageBase;
        }


        public static string GetMappedImagePathName(IntPtr hProcess, IntPtr pMemory)
        {
            int nReturnedLength;
            string mappedImagePathName;
            string driveLetter = Environment.GetEnvironmentVariable("SystemDrive");
            string devicePathName = ConvertDriveLetterToDeviceName(driveLetter);
            var imagePathName = new StringBuilder((int)Win32Consts.MAX_PATH);

            nReturnedLength = NativeMethods.GetMappedFileName(
                hProcess,
                pMemory,
                imagePathName,
                (int)Win32Consts.MAX_PATH);

            if (nReturnedLength == 0)
                mappedImagePathName = null;
            else
                mappedImagePathName = imagePathName.ToString();

            imagePathName.Clear();

            if (!string.IsNullOrEmpty(mappedImagePathName) &&
                !string.IsNullOrEmpty(devicePathName))
            {
                mappedImagePathName = Regex.Replace(
                    mappedImagePathName,
                    string.Format(@"^{0}", devicePathName).Replace("\\", "\\\\"),
                    driveLetter,
                    RegexOptions.IgnoreCase);
            }

            return mappedImagePathName;
        }


        public static bool GetMemoryBasicInformation(
            IntPtr hProcess,
            IntPtr pMemory,
            out MEMORY_BASIC_INFORMATION memoryBasicInfo)
        {
            NTSTATUS ntstatus;
            int nInfoBufferSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            IntPtr pInfoBuffer = Marshal.AllocHGlobal(nInfoBufferSize);

            ntstatus = NativeMethods.NtQueryVirtualMemory(
                    hProcess,
                    pMemory,
                    MEMORY_INFORMATION_CLASS.MemoryBasicInformation,
                    pInfoBuffer,
                    new SIZE_T((uint)nInfoBufferSize),
                    IntPtr.Zero);

            if (ntstatus == Win32Consts.STATUS_SUCCESS)
            {
                memoryBasicInfo = (MEMORY_BASIC_INFORMATION)Marshal.PtrToStructure(
                    pInfoBuffer,
                    typeof(MEMORY_BASIC_INFORMATION));
                Marshal.FreeHGlobal(pInfoBuffer);

                return true;
            }
            else
            {
                memoryBasicInfo = new MEMORY_BASIC_INFORMATION();
                Marshal.FreeHGlobal(pInfoBuffer);

                return false;
            }
        }


        public static IntPtr GetPebAddress(IntPtr hProcess)
        {
            if (!GetProcessBasicInformation(
                hProcess,
                out PROCESS_BASIC_INFORMATION pbi))
            {
                return IntPtr.Zero;
            }
            else
            {
                return pbi.PebBaseAddress;
            }
        }


        public static bool GetProcessBasicInformation(
            IntPtr hProcess,
            out PROCESS_BASIC_INFORMATION pbi)
        {
            NTSTATUS ntstatus;
            var nSizeBuffer = (uint)Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION));
            IntPtr pInfoBuffer = Marshal.AllocHGlobal((int)nSizeBuffer);

            ntstatus = NativeMethods.NtQueryInformationProcess(
                hProcess,
                PROCESS_INFORMATION_CLASS.ProcessBasicInformation,
                pInfoBuffer,
                nSizeBuffer,
                IntPtr.Zero);

            if (ntstatus != Win32Consts.STATUS_SUCCESS)
            {
                pbi = new PROCESS_BASIC_INFORMATION();
            }
            else
            {
                pbi = (PROCESS_BASIC_INFORMATION)Marshal.PtrToStructure(
                    pInfoBuffer,
                    typeof(PROCESS_BASIC_INFORMATION));
            }

            Marshal.FreeHGlobal(pInfoBuffer);

            return (ntstatus == Win32Consts.STATUS_SUCCESS);
        }


        public static IntPtr GetProcessParameters(IntPtr hProcess, IntPtr pPeb)
        {
            int nProcessParametersOffset;
            IntPtr pBufferToRead;
            IntPtr pRemoteProcessParameters;
            IntPtr pProcessParameters;
            var nStructSize = (uint)Marshal.SizeOf(
                typeof(RTL_USER_PROCESS_PARAMETERS));

            if (IntPtr.Size == 8)
            {
                nProcessParametersOffset = Marshal.OffsetOf(
                    typeof(PEB64_PARTIAL),
                    "ProcessParameters").ToInt32();
            }
            else
            {
                nProcessParametersOffset = Marshal.OffsetOf(
                    typeof(PEB32_PARTIAL),
                    "ProcessParameters").ToInt32();
            }

            pBufferToRead = ReadMemory(
                hProcess,
                new IntPtr(pPeb.ToInt64() + nProcessParametersOffset),
                (uint)IntPtr.Size);

            if (pBufferToRead == IntPtr.Zero)
                return IntPtr.Zero;

            pRemoteProcessParameters = Marshal.ReadIntPtr(pBufferToRead);
            Marshal.FreeHGlobal(pBufferToRead);

            pProcessParameters = ReadMemory(
                hProcess,
                pRemoteProcessParameters,
                nStructSize);

            if (pProcessParameters == IntPtr.Zero)
                return IntPtr.Zero;

            return pProcessParameters;
        }


        public static string GetWin32ErrorMessage(int code, bool isNtStatus)
        {
            int nReturnedLength;
            ProcessModuleCollection modules;
            FormatMessageFlags dwFlags;
            int nSizeMesssage = 256;
            var message = new StringBuilder(nSizeMesssage);
            IntPtr pNtdll = IntPtr.Zero;

            if (isNtStatus)
            {
                modules = Process.GetCurrentProcess().Modules;

                foreach (ProcessModule mod in modules)
                {
                    if (string.Compare(
                        Path.GetFileName(mod.FileName),
                        "ntdll.dll",
                        StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        pNtdll = mod.BaseAddress;
                        break;
                    }
                }

                dwFlags = FormatMessageFlags.FORMAT_MESSAGE_FROM_HMODULE |
                    FormatMessageFlags.FORMAT_MESSAGE_FROM_SYSTEM;
            }
            else
            {
                dwFlags = FormatMessageFlags.FORMAT_MESSAGE_FROM_SYSTEM;
            }

            nReturnedLength = NativeMethods.FormatMessage(
                dwFlags,
                pNtdll,
                code,
                0,
                message,
                nSizeMesssage,
                IntPtr.Zero);

            if (nReturnedLength == 0)
            {
                return string.Format("[ERROR] Code 0x{0}", code.ToString("X8"));
            }
            else
            {
                return string.Format(
                    "[ERROR] Code 0x{0} : {1}",
                    code.ToString("X8"),
                    message.ToString().Trim());
            }
        }


        public static bool IsImageBasePage(
            IntPtr hProcess,
            MEMORY_BASIC_INFORMATION memoryBasicInfo)
        {
            IntPtr pPageData;
            IntPtr pBaseAddress = memoryBasicInfo.BaseAddress;
            uint nSizeToRead = memoryBasicInfo.RegionSize.ToUInt32();
            IMAGE_DOS_HEADER imageDosHeader;

            pPageData = ReadMemory(hProcess, pBaseAddress, nSizeToRead);

            if (pPageData == IntPtr.Zero)
                return false;

            imageDosHeader = (IMAGE_DOS_HEADER)Marshal.PtrToStructure(
                pPageData,
                typeof(IMAGE_DOS_HEADER));
            Marshal.FreeHGlobal(pPageData);

            return imageDosHeader.IsValid;
        }


        public static bool IsReadableAddress(IntPtr hProcess, IntPtr pMemory)
        {
            bool status = GetMemoryBasicInformation(
                hProcess,
                pMemory,
                out MEMORY_BASIC_INFORMATION mbi);

            if (status)
            {
                return ((mbi.Protect != MEMORY_PROTECTION.NONE) && (mbi.Protect != MEMORY_PROTECTION.PAGE_NOACCESS));
            }
            else
            {
                return false;
            }
        }



        public static IntPtr ReadMemory(
            IntPtr hProcess,
            IntPtr pReadAddress,
            uint nSizeToRead)
        {
            NTSTATUS ntstatus;
            IntPtr pBuffer = Marshal.AllocHGlobal((int)nSizeToRead);
            ZeroMemory(pBuffer, (int)nSizeToRead);

            ntstatus = NativeMethods.NtReadVirtualMemory(
                    hProcess,
                    pReadAddress,
                    pBuffer,
                    nSizeToRead,
                    IntPtr.Zero);

            if (ntstatus != Win32Consts.STATUS_SUCCESS)
            {
                Marshal.FreeHGlobal(pBuffer);

                return IntPtr.Zero;
            }

            return pBuffer;
        }


        public static string ReadRemoteUnicodeString(
            IntPtr hProcess,
            UNICODE_STRING unicodeString)
        {
            string result;
            IntPtr unicodeBuffer = unicodeString.GetBuffer();

            if (unicodeBuffer == IntPtr.Zero)
                return null;

            IntPtr pBuffer = ReadMemory(
                hProcess,
                unicodeBuffer,
                unicodeString.MaximumLength);

            if (pBuffer == IntPtr.Zero)
                return null;

            result = Marshal.PtrToStringUni(pBuffer);
            Marshal.FreeHGlobal(pBuffer);

            return result;
        }


        public static string ResolveImagePathName(string commandLine)
        {
            int returnedLength;
            int nCountQuotes;
            string fileName;
            string extension;
            string imagePathName = null;
            string[] arguments = Regex.Split(commandLine.Trim(), @"\s+");
            var candidatePath = new StringBuilder(Win32Consts.MAX_PATH);
            var resolvedPath = new StringBuilder(Win32Consts.MAX_PATH);
            var regexExtension = new Regex(@".+\.\S+$");
            var regexExe = new Regex(@".+\.exe$");

            for (var idx = 0; idx < arguments.Length; idx++)
            {
                if (idx > 0)
                    candidatePath.Append(" ");

                candidatePath.Append(arguments[idx]);
                fileName = candidatePath.ToString();

                nCountQuotes = Regex.Matches(fileName, "\"").Count;

                if (((nCountQuotes % 2) != 0) && (nCountQuotes > 0))
                {
                    continue;
                }
                else if (nCountQuotes == 0)
                {
                    nCountQuotes = Regex.Matches(fileName, "\'").Count;

                    if (((nCountQuotes % 2) != 0) && (nCountQuotes > 0))
                        continue;
                    else
                        fileName = fileName.Trim('\'');
                }
                else
                {
                    fileName = fileName.Trim('\"');
                }

                extension = regexExtension.IsMatch(fileName) ? null : ".exe";

                try
                {
                    imagePathName = Path.GetFullPath(fileName);
                }
                catch
                {
                    imagePathName = null;

                    break;
                }

                if (File.Exists(imagePathName) && regexExe.IsMatch(imagePathName))
                {
                    break;
                }
                else
                {
                    returnedLength = NativeMethods.SearchPath(
                        null,
                        fileName,
                        extension,
                        Win32Consts.MAX_PATH,
                        resolvedPath,
                        IntPtr.Zero);

                    if (returnedLength > 0)
                    {
                        imagePathName = resolvedPath.ToString();

                        if (regexExe.IsMatch(imagePathName))
                            break;
                    }
                }

                resolvedPath.Clear();
                resolvedPath.Capacity = Win32Consts.MAX_PATH;
                imagePathName = null;
            }

            candidatePath.Clear();
            resolvedPath.Clear();

            return imagePathName;
        }


        public static bool WriteDataIntoFile(
            IntPtr hFile,
            IntPtr pBuffer,
            uint nBufferSize)
        {
            NTSTATUS ntstatus;
            IntPtr pIoStatusBlock = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IO_STATUS_BLOCK)));

            ntstatus = NativeMethods.NtWriteFile(
                hFile,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                pIoStatusBlock,
                pBuffer,
                nBufferSize,
                IntPtr.Zero,
                IntPtr.Zero);
            Marshal.FreeHGlobal(pIoStatusBlock);

            return (ntstatus == Win32Consts.STATUS_SUCCESS);
        }


        public static void ZeroMemory(IntPtr buffer, int size)
        {
            var nullBytes = new byte[size];
            Marshal.Copy(nullBytes, 0, buffer, size);
        }


        public static void ZeroMemory(ref byte[] bytes, int size)
        {
            var nullBytes = new byte[size];
            Buffer.BlockCopy(nullBytes, 0, bytes, 0, size);
        }
    }
}
