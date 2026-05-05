using System;

namespace RealEstateMinsk.Services;

public class UpdateProgressService
{
    public double Progress { get; private set; }
    public string Status { get; private set; } = "";
    public string Region { get; private set; } = "";
    public int Page { get; private set; }
    public int MaxPages { get; private set; }
    public bool IsActive { get; private set; }

    public event Action? OnProgressUpdated;

    public void Report(double progress, string status, string region = "", int page = 0, int maxPages = 0)
    {
        Progress = Math.Clamp(progress, 0, 100);
        Status = status;
        Region = region;
        Page = page;
        MaxPages = maxPages;
        IsActive = true;
        
        Notify();
    }

    public void Start(string status)
    {
        Progress = 0;
        Status = status;
        Region = "";
        Page = 0;
        MaxPages = 0;
        IsActive = true;
        Notify();
    }

    public void Reset()
    {
        IsActive = false;
        Progress = 0;
        Status = "";
        Region = "";
        Page = 0;
        MaxPages = 0;
        Notify();
    }

    private void Notify() => OnProgressUpdated?.Invoke();
}
