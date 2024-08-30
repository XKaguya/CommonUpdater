using System.Diagnostics;

class UpdaterHelper
{
    static void Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.WriteLine("Usage: UpdaterHelper <parentProcessId> <targetExePath> <newExePath>");
            return;
        }

        string parentProcessIdStr = args[0];
        string targetExePath = args[1];
        string newExePath = args[2];
        
        Console.WriteLine($"{parentProcessIdStr} {targetExePath} {newExePath}");

        if (!int.TryParse(parentProcessIdStr, out int parentProcessId))
        {
            Console.WriteLine("Invalid parent process ID.");
            return;
        }
        
        if (parentProcessId != 0)
        {
            Process.GetProcessById(parentProcessId)?.Kill();
            Thread.Sleep(500);
        }
        
        Thread.Sleep(500);

        try
        {
            if (File.Exists(targetExePath))
            {
                File.Delete(targetExePath);
            }
            
            File.Move(newExePath, targetExePath);
            
            Process.Start(targetExePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update failed: {ex.Message}");
        }
    }
}