public interface IJobStore
{
    DocumentJob Create(string fileName);
    DocumentJob? Get(string id);
    void Update(string id, Action<DocumentJob> update);
}
