namespace WingetOffline.Core.Install;

/// <summary>رموز الخروج المقبولة كنجاح، ورسائل مفهومة لأشهر أخطاء Windows Installer.</summary>
public static class ExitCodes
{
    public static bool IsRebootCode(int code) => code is 3010 or 1641;

    public static bool IsSuccess(int code, bool isUninstall, bool nsisStyleUninstaller)
    {
        if (code == 0 || IsRebootCode(code)) return true;
        // الإزالة: "المنتج غير مثبّت" أو "أُزيل بالفعل" تعني أن الهدف تحقق
        if (isUninstall && (code == 1605 || code == 1614)) return true;
        // سلوك معروف لمزيلات NSIS
        if (isUninstall && nsisStyleUninstaller && code == 1627) return true;
        return false;
    }

    public static string Describe(int code) => code switch
    {
        0 => "Completed successfully.",
        5 => "Access denied (5).",
        740 => "Administrator permission is required (740).",
        1223 => "The operation was cancelled by the user (1223).",
        1602 => "The user cancelled the installation (1602).",
        1603 => "Fatal error during installation (MSI 1603). Check the log for details.",
        1605 => "This action is only valid for products that are currently installed (1605).",
        1614 => "The product is uninstalled (1614).",
        1618 => "Another installation is already in progress (1618). Finish it and try again.",
        1619 => "The installation package could not be opened (1619).",
        1620 => "The installation package is invalid (1620).",
        1625 => "The installation is blocked by a system policy (1625).",
        1638 => "Another version of this product is already installed (1638).",
        1641 => "Completed; the installer started a reboot (1641).",
        3010 => "Completed; a reboot is required (3010).",
        _ => $"The process finished with exit code {code}.",
    };
}
