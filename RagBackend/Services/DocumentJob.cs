public enum JobStatus { Queued, Processing, Completed, Failed }

public class DocumentJob
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public string FileName { get; init; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}
