using CommunityToolkit.Mvvm.ComponentModel;
using WingetOffline.Core.SelfTest;

namespace WingetOffline.UI.ViewModels;

/// <summary>صف نتيجة فحص آلي في الجدول.</summary>
public sealed class CheckRow
{
    public CheckRow(CheckResult r) => Result = r;
    public CheckResult Result { get; }
    public string Status => Result.Status.ToString().ToUpperInvariant();
    public string Id => Result.Id;
    public string Category => Result.Category;
    public string Name => Result.Name;
    public string PlanIds => Result.PlanIds;
    public long Ms => Result.DurationMs;
    public string Message => Result.Message;
    public string Details => string.IsNullOrWhiteSpace(Result.Details) ? Result.Message : Result.Message + "\n\n" + Result.Details;
}

/// <summary>خانة في قائمة الفحص اليدوي: نتيجة وملاحظة يحفظهما البرنامج ويضمّنهما التقرير.</summary>
public sealed partial class ManualRow : ObservableObject
{
    public static string[] ResultOptions { get; } = { "NotTested", "Pass", "Fail" };

    public ManualRow(ManualCheck check, ManualResult? saved)
    {
        Check = check;
        _result = saved?.Result is "Pass" or "Fail" ? saved.Result : "NotTested";
        _note = saved?.Note ?? "";
    }

    public ManualCheck Check { get; }
    public string Id => Check.Id;
    public string Priority => Check.Priority;
    public string Action => Check.Action;
    public string Expected => Check.Expected;

    [ObservableProperty] private string _result;
    [ObservableProperty] private string _note;

    public ManualResult ToResult() => new() { Id = Id, Result = Result, Note = Note ?? "" };
}
