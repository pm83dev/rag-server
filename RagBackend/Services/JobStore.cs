using System.Collections.Concurrent;

public class JobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, DocumentJob> _jobs = new();

    public DocumentJob Create(string fileName)
    {
        var job = new DocumentJob { FileName = fileName };
        _jobs[job.Id] = job;
        return job;
    }

    public DocumentJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void Update(string id, Action<DocumentJob> update)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                update(job);
            }
        }
    }
}
