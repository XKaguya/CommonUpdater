using System.Diagnostics;
using Newtonsoft.Json.Linq;

class CommonUpdater
{
    public static async Task Main(string[] args)
    {
        try
        {
            if (args.Length != 6)
            {
                Console.WriteLine("Usage: CommonUpdater <projectName> <projectExeName> <projectAuthor> <projectCurrentVersion> <projectCurrentExePath> <projectNewExePath>");
                return;
            }

            string projectName = args[0];
            string projectExeName = args[1];
            string projectAuthor = args[2];
            string projectCurrentVersion = args[3];
            string projectCurrentExePath = args[4];
            string projectNewExePath = args[5];

            var newestVersion = await GetLatestReleaseTagAsync(projectName, projectAuthor);

            var currentVersion = Version.Parse(projectCurrentVersion);
            var newVersion = Version.Parse(newestVersion);

            if (currentVersion == newVersion)
            {
                Console.WriteLine("You are already using the latest version.");
                return;
            }
            if (currentVersion > newVersion)
            {
                Console.WriteLine("You are using testing version.");
                return;
            }

            await DownloadFileAsync(projectName, projectExeName, projectAuthor, projectNewExePath);

            KillExistingInstances(projectExeName);

            ReplaceOldFile(projectCurrentExePath, projectNewExePath);

            Process.Start(projectCurrentExePath);
            
            Console.WriteLine("Project update successfully.");
            
            Console.WriteLine("Press Enter to continue...");

            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update failed: {ex.Message}");
        }
    }

    private static async Task<string> GetLatestReleaseTagAsync(string projectName, string projectAuthor)
    {
        try
        {
            string url = $"https://api.github.com/repos/{projectAuthor}/{projectName}/releases/latest";
            using HttpClient httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("request");

            string responseBody = await httpClient.GetStringAsync(url);
            JObject json = JObject.Parse(responseBody);

            var version = json["tag_name"]?.ToString();
            
            Console.WriteLine($"Project Name: {projectName}");
            Console.WriteLine($"Project Author: {projectAuthor}");
            Console.WriteLine($"Project Newest Version On Github: {version}");

            return version;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching latest release: {ex.Message}");
            return null;
        }
    }

    private static async Task DownloadFileAsync(string projectName, string projectExeName, string projectAuthor, string projectNewExePath)
    {
        try
        {
            if (File.Exists(projectNewExePath))
            {
                File.Delete(projectNewExePath);
            }

            string url = $"https://github.com/{projectAuthor}/{projectName}/releases/latest/download/{projectExeName}";
            using HttpClient httpClient = new HttpClient();
            
            Console.WriteLine($"Downloading the newest exe from {url}");

            await using var stream = await httpClient.GetStreamAsync(url);
            await using var fileStream = new FileStream(projectNewExePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
            
            Console.WriteLine($"Download successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
        }
    }

    private static void KillExistingInstances(string projectExeName)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(projectExeName));

        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to kill process {process.Id}: {ex.Message}");
            }
        }
    }

    private static void ReplaceOldFile(string projectCurrentExePath, string projectNewExePath)
    {
        try
        {
            if (File.Exists(projectCurrentExePath))
            {
                File.Delete(projectCurrentExePath);
            }

            File.Move(projectNewExePath, projectCurrentExePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error replacing old file: {ex.Message}");
        }
    }
}