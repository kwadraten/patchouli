using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32;
using Patchouli.UI.Services;

namespace Patchouli.Tests;

public sealed class ProtocolRegistrationTests
{
    [Fact]
    public void Command_quotes_the_executable_and_the_complete_uri()
    {
        ProtocolRegistrationService.BuildCommand(@"C:\Program Files\Patchouli.NET\Patchouli.UI.exe")
            .Should().Be("\"C:\\Program Files\\Patchouli.NET\\Patchouli.UI.exe\" --open-uri \"%1\"");
    }

    [Fact]
    public void Windows_registration_roundtrips_and_never_removes_another_owner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyWindowsRegistration();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsRegistration()
    {
        // A private test key exercises the real registry without changing any URL association.
        string keyPath = @"Software\Patchouli.Tests\Protocol-" + Guid.NewGuid().ToString("N");
        ProtocolRegistrationService service = new(keyPath, typeof(ProtocolRegistrationTests).Assembly.Location);
        try
        {
            service.GetState().IsRegistered.Should().BeFalse();
            service.SetRegistered(true);
            service.GetState().IsRegistered.Should().BeTrue();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                key.GetValue("URL Protocol").Should().Be("");
                using RegistryKey command = key.OpenSubKey(@"shell\open\command")!;
                command.GetValue(null).Should().Be(ProtocolRegistrationService.BuildCommand(
                    typeof(ProtocolRegistrationTests).Assembly.Location));
            }

            service.SetRegistered(false);
            service.GetState().IsRegistered.Should().BeFalse();
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue(ProtocolRegistrationService.OwnerValue, "someone-else");
            }

            Action unregister = () => service.SetRegistered(false);
            unregister.Should().Throw<InvalidOperationException>();
            Action register = () => service.SetRegistered(true);
            register.Should().Throw<InvalidOperationException>();
            using RegistryKey remaining = Registry.CurrentUser.OpenSubKey(keyPath)!;
            remaining.GetValue(ProtocolRegistrationService.OwnerValue).Should().Be("someone-else");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }
    }
}
