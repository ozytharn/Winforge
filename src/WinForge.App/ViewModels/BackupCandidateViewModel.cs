using WinForge.Domain.Recovery;

namespace WinForge.App.ViewModels;

public sealed class BackupCandidateViewModel : ViewModelBase
{
    private string _latestInspectionState;
    private string _latestInspectionTime;
    private DateTimeOffset? _latestInspectionAt;

    public BackupCandidateViewModel(
        BackupCandidate candidate,
        string latestInspectionState,
        DateTimeOffset? latestInspectionAt)
    {
        Candidate = candidate;
        _latestInspectionState = latestInspectionState;
        _latestInspectionAt = latestInspectionAt;
        _latestInspectionTime = FormatInspectionTime(latestInspectionAt);
    }

    public BackupCandidate Candidate { get; }

    public string LatestInspectionState
    {
        get => _latestInspectionState;
        private set => SetProperty(ref _latestInspectionState, value);
    }

    public string LatestInspectionTime
    {
        get => _latestInspectionTime;
        private set => SetProperty(ref _latestInspectionTime, value);
    }

    public DateTimeOffset? LatestInspectionAt
    {
        get => _latestInspectionAt;
        private set
        {
            if (SetProperty(ref _latestInspectionAt, value))
            {
                LatestInspectionTime = FormatInspectionTime(value);
            }
        }
    }

    public void UpdateLatestInspection(string state, DateTimeOffset? latestInspectionAt)
    {
        LatestInspectionState = state;
        LatestInspectionAt = latestInspectionAt;
    }

    private static string FormatInspectionTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g") ?? "Not inspected";
}
