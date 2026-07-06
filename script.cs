using System;
using Microsoft.Data.Sqlite;

class Program
{
    static void Main()
    {
        string dbPath = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Patchouli\patchouli-runtime.sqlite");
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pages WHERE document_instance_id = (SELECT document_instance_id FROM items WHERE title LIKE '%Strangers in a Strange Land%')";
            Console.WriteLine("Pages count: " + command.ExecuteScalar());
            
            command.CommandText = "SELECT COUNT(*) FROM layout_nodes WHERE document_instance_id = (SELECT document_instance_id FROM items WHERE title LIKE '%Strangers in a Strange Land%')";
            Console.WriteLine("Layout nodes: " + command.ExecuteScalar());
        }
    }
}
