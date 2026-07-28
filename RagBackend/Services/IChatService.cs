using System.Threading.Tasks;

public interface IChatService
{
    Task<string> AskAsync(string question, string context);
}
