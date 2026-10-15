using System.Security.Cryptography;
namespace Shop.Api.Data;
public class Repo
{
    private const string ConnectionString = "Host=db;Username=app;Password=hunter22";
    private readonly string apiKey = "sk-live-0123456789abcdefghijklmnop";
    public object Find(string id)
    {
        var cmd = new SqlCommand("SELECT * FROM orders WHERE id = '" + id + "'");
        var h = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return cmd;
    }
}
public class SqlCommand(string text) { public string Text { get; } = text; }
