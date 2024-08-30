using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class CommonUpdater
{
    private static readonly string LogFilePath = "CommonUpdater.log";
    private static readonly string ServerUrl = "SERVER_ADDRESS";
    private static readonly string ProgramVersion = "1.0.0";
    private const int MaxRetryCount = 3;

    public static async Task Main(string[] args)
    {
        Log($"CommonUpdater version: {ProgramVersion}");
        
        await File.WriteAllTextAsync(LogFilePath, string.Empty);
        
        try
        {
            if (args.Length != 6)
            {
                Log("Usage: CommonUpdater <projectName> <projectExeName> <projectAuthor> <projectCurrentVersion> <projectCurrentExePath> <projectNewExePath>");
                return;
            }

            string projectName = args[0];
            string projectExeName = args[1];
            string projectAuthor = args[2];
            string projectCurrentVersion = args[3];
            string projectCurrentExePath = args[4];
            string projectNewExePath = args[5];

            var newestVersion = await GetLatestVersionWithRetryAsync(projectName, projectAuthor);

            var currentVersion = Version.Parse(projectCurrentVersion);
            var newVersion = Version.Parse(newestVersion);

            if (currentVersion == newVersion)
            {
                Log("You are already using the latest version.");
                Environment.Exit(0);
                return;
            }
            if (currentVersion > newVersion)
            {
                Log("You are using a testing version.");
                Environment.Exit(0);
                return;
            }
            
            Log($"Program current version: {currentVersion}");

            await DownloadFileWithRetryAsync(projectName, projectExeName, projectAuthor, projectNewExePath);

            KillExistingInstances(projectExeName);

            ReplaceOldFile(projectCurrentExePath, projectNewExePath);

            Process.Start(projectCurrentExePath);
            
            Log("Project update successfully.");
            
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex.Message}");
        }
    }

    private static async Task<string> GetLatestVersionWithRetryAsync(string projectName, string projectAuthor)
    {
        string version = await RetryAsync(() => GetLatestVersionFromServer(projectName)) 
                      ?? await RetryAsync(() => GetLatestReleaseTagAsync(projectName, projectAuthor));
        return version ?? throw new Exception("Failed to get the latest version.");
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
            
            Log($"Project Name: {projectName}");
            Log($"Project Author: {projectAuthor}");
            Log($"Project Newest Version On Github: {version}");

            return version;
        }
        catch (Exception ex)
        {
            Log($"Error fetching latest release: {ex.Message}");
            return null;
        }
    }

    private static async Task DownloadFileWithRetryAsync(string projectName, string projectExeName, string projectAuthor, string projectNewExePath)
    {
        if (!await RetryAsync(() => DownloadFileFromServerAsync(projectName, projectExeName, projectNewExePath)))
        {
            await RetryAsync(() => DownloadFileAsync(projectName, projectExeName, projectAuthor, projectNewExePath));
        }
    }

    private static async Task<string> GetLatestVersionFromServer(string projectName)
    {
        try
        {
            using HttpClient httpClient = new HttpClient();

            var url = ServerUrl + "Versions.json";
        
            HttpResponseMessage response = await httpClient.GetAsync(url);
            
            response.EnsureSuccessStatusCode();
            
            string jsonResponse = await response.Content.ReadAsStringAsync();
            
            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);
            
            if (jsonData != null && jsonData.TryGetValue(projectName, out string? version))
            {
                Log($"Project Name: {projectName}");
                Log($"Newest Version on Server: {version}");
                return version;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log($"Error fetching version from server: {ex.Message}");
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
            
            Log($"Downloading the newest exe from {url}");

            await using var stream = await httpClient.GetStreamAsync(url);
            await using var fileStream = new FileStream(projectNewExePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
            
            Log($"Download successfully.");
        }
        catch (Exception ex)
        {
            Log($"Error downloading file from GitHub: {ex.Message}");
        }
    }

    private static async Task DownloadFileFromServerAsync(string projectName, string projectExeName, string projectNewExePath)
    {
        try
        {
            if (File.Exists(projectNewExePath))
            {
                File.Delete(projectNewExePath);
            }

            string url = $"{ServerUrl}/{projectName}/{projectExeName}";
            using HttpClient httpClient = new HttpClient();
            
            Log($"Downloading the newest exe from {url}");

            await using var stream = await httpClient.GetStreamAsync(url);
            await using var fileStream = new FileStream(projectNewExePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
            
            Log($"Download successfully.");
        }
        catch (Exception ex)
        {
            Log($"Error downloading file from server: {ex.Message}");
        }
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation)
    {
        for (int i = 0; i < MaxRetryCount; i++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                Log($"Attempt {i + 1} failed: {ex.Message}");
                if (i == MaxRetryCount - 1) throw;
            }
            await Task.Delay(1000);
        }
        return default;
    }

    private static async Task<bool> RetryAsync(Func<Task> operation)
    {
        for (int i = 0; i < MaxRetryCount; i++)
        {
            try
            {
                await operation();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Attempt {i + 1} failed: {ex.Message}");
                if (i == MaxRetryCount - 1) throw;
            }
            await Task.Delay(1000);
        }
        return false;
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
                Log($"Killed process {process.Id}");
            }
            catch (Exception ex)
            {
                Log($"Failed to kill process {process.Id}: {ex.Message}");
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
            Log("Replaced old file with new version.");
        }
        catch (Exception ex)
        {
            Log($"Error replacing old file: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
        Console.WriteLine(logMessage);

        try
        {
            File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write log to file: {ex.Message}");
        }
    }
}