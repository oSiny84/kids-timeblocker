using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Security;

/// <summary>
/// 데이터 폴더(%ProgramData%\TimeBlocker)의 권한을 정리한다.
///
/// 목표: 일반 사용자 계정으로는 설정/허용 상태/로그를 수정할 수 없게 한다.
///   SYSTEM         : 모든 권한
///   Administrators : 모든 권한
///   현재 실행 계정  : 모든 권한 (서비스가 자기 폴더에 못 쓰는 상황을 막기 위함)
///   Users          : 읽기만
///
/// Windows 표준 ACL 만 사용한다. 파일을 숨기거나 보안 제품을 우회하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryHardener
{
    public static void Harden(string directory, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            // 관리자 권한이 없으면 건드리지 않는다.
            // 권한 없는 계정이 ACL 을 바꾸면 자기 자신이 폴더에서 잠기는 문제가 생긴다.
            // (실제 배포에서는 LocalSystem 으로 도는 서비스가 이 코드를 실행한다)
            if (!IsElevatedOrSystem())
            {
                logger.LogInformation(
                    "관리자 권한이 아니어서 데이터 폴더 권한 설정을 건너뜁니다: {Directory}", directory);
                return;
            }

            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();

            // 상속을 끊고 기존 규칙을 비운 뒤, 원하는 규칙으로 다시 구성한다.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(rule);
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            // 현재 실행 계정에도 반드시 쓰기 권한을 남긴다.
            // (LocalSystem 이면 위 규칙과 같고, 관리자 콘솔 실행이면 그 계정이 추가된다)
            var current = GetCurrentUserSid();
            if (current is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    current, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }

            // 일반 사용자는 읽기만 가능. (로그를 확인할 수는 있지만 고칠 수는 없다)
            security.AddAccessRule(new FileSystemAccessRule(
                users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);

            logger.LogInformation("데이터 폴더 권한을 적용했습니다: {Directory}", directory);
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning("데이터 폴더 권한을 바꿀 수 없습니다. 관리자 권한으로 실행했는지 확인하세요: {Directory}", directory);
        }
        catch (Exception ex)
        {
            // 권한 설정 실패로 서비스가 시작되지 못하면 안 된다.
            logger.LogWarning(ex, "데이터 폴더 권한 적용에 실패했습니다: {Directory}", directory);
        }
    }

    private static bool IsElevatedOrSystem()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem) return true;

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static SecurityIdentifier? GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch
        {
            return null;
        }
    }
}
