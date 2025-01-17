using System.Diagnostics;
using System.Reflection;
using System.Text;
using log4net;
using log4net.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace CommonUpdater
{
    class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));
        private static string _branch = "Release";
        private static readonly string Branch = _branch;
        private const string ServerUrl = "http://kaguya.net.cn:9999";
        private const string ProgramVersion = "1.0.6";
        private const int MaxRetryCount = 3;
        private static readonly ProjectInfo ProjectInfo = new();
        private static readonly ProjectInfo ProjectInfoSelf = new();

        public static async Task Main(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "CommonUpdater.log4net.config";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                XmlConfigurator.Configure(stream);
            }
            else
            {
                Log.Error($"Failed to find embedded resource: {resourceName}");
            }

            if (File.Exists("DevMode"))
            {
                _branch = "Dev";
            }
            
            Log.Info($"CommonUpdater version: {ProgramVersion}");
            
            ProjectInfo.ProjectName = args[0];
            ProjectInfo.ProjectExeName = args[1];
            ProjectInfo.ProjectAuthor = args[2];
            ProjectInfo.ProjectCurrentVersion = args[3];
            ProjectInfo.ProjectCurrentExePath = args[4];
            ProjectInfo.ProjectNewExePath = args[5];

            await SelfUpdateAsync();

            try
            {
                if (args.Length != 6)
                {
                    Log.Info("Usage: CommonUpdater <projectName> <projectExeName> <projectAuthor> <projectCurrentVersion> <projectCurrentExePath> <projectNewExePath>");
                    return;
                }

                Log.Debug(ProjectInfo.ToString());
                
                if (await GetIfServerUnderMaintenanceMode(ProjectInfo) == "MAINTENANCE" && Branch != "Dev")
                {
                    Log.Info($"Update Server is currently under maintenance mode.");
                    Log.Info($"Current branch is {Branch}");
                    Log.Info("Now exiting...");
                    Environment.Exit(0);
                    return;
                }

                var newestVersion = await GetLatestVersionWithRetryAsync(ProjectInfo);
                
                ProjectInfo.ProjectNewVersion = newestVersion;
                
                var currentVersion = Version.Parse(ProjectInfo.ProjectCurrentVersion);
                var newVersion = Version.Parse(newestVersion);

                if (currentVersion == newVersion)
                {
                    Log.Info("You are already using the latest version.");
                    Environment.Exit(0);
                    return;
                }

                if (currentVersion > newVersion)
                {
                    Log.Info("You are using a testing version.");
                    Environment.Exit(0);
                    return;
                }

                Log.Info($"Program current version: {currentVersion}");

                await DownloadFileWithRetryAsync(ProjectInfo);

                KillExistingInstances(ProjectInfo.ProjectExeName);

                string tempDir = Path.GetTempPath();
                ExtractAndRunUpdaterHelper(0, tempDir);

                Process.Start(ProjectInfo.ProjectCurrentExePath);

                Log.Info("Project update successfully.");

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log.Error($"Update failed: {ex.Message}");
            }
        }

        private static async Task<string> GetLatestVersionWithRetryAsync(ProjectInfo projectInfo)
        {
            string? version = await RetryAsync(() => GetLatestVersionFromServer(projectInfo)) ?? await RetryAsync(() => GetLatestReleaseTagAsync(projectInfo));
            return version ?? throw new Exception("Failed to get the latest version.");
        }
        
        private static async Task<string?> GetIfServerUnderMaintenanceMode(ProjectInfo projectInfo)
        {
            using HttpClient httpClient = new HttpClient();

            var url = $"{ServerUrl}/Versions.json";

            string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo?.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            HttpResponseMessage response = await httpClient.GetAsync(url);

            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();

            var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);
            
            if (jsonData != null && jsonData.TryGetValue("MaintenanceMode", out string? isServerUnderMaintenanceMode))
            {
                if (bool.TryParse(isServerUnderMaintenanceMode, out bool isServerUnderMaintenance))
                {
                    if (isServerUnderMaintenance)
                    {
                        return "MAINTENANCE";
                    }
                    else
                    {
                        return "ONLINE";
                    }
                }
            }
            else
            {
                return "UNKNOWN";
            }
            
            return null;
        }

        private static async Task<string?> GetLatestReleaseTagAsync(ProjectInfo projectInfo)
        {
            try
            {
                string url = $"https://api.github.com/repos/{projectInfo.ProjectAuthor}/{projectInfo.ProjectName}/releases/latest";
                using HttpClient httpClient = new HttpClient();

                httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("request");

                string responseBody = await httpClient.GetStringAsync(url);
                JObject json = JObject.Parse(responseBody);

                var version = json["tag_name"]?.ToString();

                Log.Info($"Project Name: {projectInfo.ProjectName}");
                Log.Info($"Project Author: {projectInfo.ProjectAuthor}");
                Log.Info($"Project Newest Version On Github: {version}");

                return version;
            }
            catch (Exception ex)
            {
                Log.Error($"Error fetching latest release: {ex.Message}");
                return null;
            }
        }

        private static async Task DownloadFileWithRetryAsync(ProjectInfo projectInfo)
        {
            if (!await RetryAsync(() => DownloadFileFromServerAsync(projectInfo)))
            {
                await RetryAsync(() => DownloadFileAsync(projectInfo));
            }
        }

        private static async Task<string?> GetLatestVersionFromServer(ProjectInfo projectInfo)
        {
            try
            {
                using HttpClient httpClient = new HttpClient();

                var url = $"{ServerUrl}/Versions.json";

                string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

                HttpResponseMessage response = await httpClient.GetAsync(url);

                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();

                var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);

                if (jsonData == null)
                {
                    return null;
                }

                if (Branch == "Dev")
                {
                    if (jsonData.TryGetValue($"{projectInfo.ProjectName}Dev", out string? version))
                    {
                        Log.Info("Branch Dev");
                        Log.Info($"Project Name: {projectInfo.ProjectName}Dev");
                        Log.Info($"Newest Version on Server: {version}");
                        return version;
                    }
                }
                else if (Branch == "Release")
                {
                    if (jsonData.TryGetValue(projectInfo.ProjectName, out string? version))
                    {
                        Log.Info($"Project Name: {projectInfo.ProjectName}");
                        Log.Info($"Newest Version on Server: {version}");
                        return version;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"Error fetching version from server: {ex.Message}");
                return null;
            }
        }

        private static async Task DownloadFileAsync(ProjectInfo projectInfo)
        {
            try
            {
                if (File.Exists(projectInfo.ProjectNewExePath))
                {
                    File.Delete(projectInfo.ProjectNewExePath);
                }

                string url = $"https://github.com/{projectInfo.ProjectAuthor}/{projectInfo.ProjectName}/releases/latest/download/{projectInfo.ProjectExeName}";
                using HttpClient httpClient = new HttpClient();

                Log.Info($"Downloading the newest exe from {url}");

                await using var stream = await httpClient.GetStreamAsync(url);
                await using var fileStream = new FileStream(projectInfo.ProjectNewExePath, FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await stream.CopyToAsync(fileStream);

                Log.Info($"Download successfully.");
            }
            catch (Exception ex)
            {
                Log.Error($"Error downloading file from GitHub: {ex.Message}");
            }
        }

        private static async Task DownloadFileFromServerAsync(ProjectInfo projectInfo)
        {
            try
            {
                if (File.Exists(projectInfo.ProjectNewExePath))
                {
                    File.Delete(projectInfo.ProjectNewExePath);
                }
                
                string url = String.Empty;
                string patchNoteUrl = String.Empty;;

                if (Branch == "Dev")
                {
                    url = $"{ServerUrl}/{projectInfo.ProjectName}Dev/{projectInfo.ProjectExeName}";
                    patchNoteUrl = $"{ServerUrl}/{projectInfo.ProjectName}Dev/PatchNote-{projectInfo.ProjectNewVersion}.txt";
                }
                if (Branch == "Release")
                {
                    url = $"{ServerUrl}/{projectInfo.ProjectName}/{projectInfo.ProjectExeName}";
                    patchNoteUrl = $"{ServerUrl}/{projectInfo.ProjectName}/PatchNote-{projectInfo.ProjectNewVersion}.txt";
                }
                
                using HttpClient httpClient = new HttpClient();
                string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo?.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

                Log.Info($"Downloading the newest exe from {url}");

                await using var stream = await httpClient.GetStreamAsync(url);
                await using var fileStream = new FileStream(projectInfo.ProjectNewExePath, FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await stream.CopyToAsync(fileStream);
                
                Log.Info($"Downloading the patch note from {patchNoteUrl}");
                
                string patchNoteContent = await httpClient.GetStringAsync(patchNoteUrl);
                StringBuilder stringBuilder = new StringBuilder();
                string[] lines = patchNoteContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                stringBuilder.AppendLine("Update Available");
                stringBuilder.AppendLine($"Current Version: {projectInfo.ProjectCurrentVersion}");
                stringBuilder.AppendLine($"New Version: {projectInfo.ProjectNewVersion}");
                stringBuilder.AppendLine("Patch Note:");

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine))
                    {
                        stringBuilder.AppendLine(trimmedLine);
                    }
                }
                
                MessageBox.Show(stringBuilder.ToString(), $"PatchNote - {projectInfo.ProjectName}", MessageBoxButtons.OK, MessageBoxIcon.Information);

                Log.Info($"Download successfully.");
            }
            catch (Exception ex)
            {
                Log.Error($"Error downloading file from server: {ex.Message}");
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
                    Log.Error($"Attempt {i + 1} failed: {ex.Message}");
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
                    Log.Error($"Attempt {i + 1} failed: {ex.Message}");
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
                    Log.Info($"Killed process {process.Id}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to kill process {process.Id}: {ex.Message}");
                }
            }
        }
        
        private static string SerializeToYaml(ProjectInfo projectInfo)
        {
            var serializer = new SerializerBuilder().Build();
            return serializer.Serialize(projectInfo);
        }

        private static void ExtractAndRunUpdaterHelper(int pid, string tempDir, bool isFromSelfUpdate = false)
        {
            string helperResourceName = "CommonUpdater.UpdaterHelper.exe";
            string helperPath = Path.Combine(tempDir, "UpdaterHelper.exe");

            try
            {
                using (var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(helperResourceName))
                {
                    if (resourceStream == null)
                        throw new Exception("Failed to load embedded UpdaterHelper.exe.");

                    using (var fileStream = new FileStream(helperPath, FileMode.Create, FileAccess.Write))
                    {
                        resourceStream.CopyTo(fileStream);
                    }
                }
                
                var yaml = SerializeToYaml(ProjectInfo);
                var processStartInfo = new ProcessStartInfo();

                if (isFromSelfUpdate)
                {
                    processStartInfo.FileName = helperPath;
                    processStartInfo.Arguments = $"\"{pid}\" \"{ProjectInfoSelf.ProjectCurrentExePath}\" \"{ProjectInfoSelf.ProjectNewExePath}\" \"{yaml}\" \"{isFromSelfUpdate.ToString()}\"";
                    processStartInfo.UseShellExecute = false;
                }
                else
                {
                    processStartInfo.FileName = helperPath;
                    processStartInfo.Arguments = $"\"{pid}\" \"{ProjectInfo.ProjectCurrentExePath}\" \"{ProjectInfo.ProjectNewExePath}\" \"{yaml}\" \"{isFromSelfUpdate.ToString()}\"";
                    processStartInfo.UseShellExecute = false;
                }

                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null)
                    {
                        throw new Exception("Failed to start UpdaterHelper.exe.");
                    }

                    Log.Debug(processStartInfo.Arguments);

                    process.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                Console.WriteLine($"Helper path: {helperPath}");
            }
        }

        private static async Task SelfUpdateAsync()
        {
            string selfUpdatePath = "CommonUpdater_New.exe";
            string selfUpdatePathOld = "CommonUpdater.exe";
            var currentVersion = Version.Parse(ProgramVersion);
            ProjectInfoSelf.ProjectName = "CommonUpdater";
            ProjectInfoSelf.ProjectCurrentVersion = ProgramVersion;
            ProjectInfoSelf.ProjectAuthor = "XKaguya";
            ProjectInfoSelf.ProjectExeName = "CommonUpdater.exe";
            ProjectInfoSelf.ProjectCurrentExePath = selfUpdatePathOld;
            ProjectInfoSelf.ProjectNewExePath = selfUpdatePath;

            Log.Debug(ProjectInfoSelf.ToString());
            
            if (await GetIfServerUnderMaintenanceMode(ProjectInfoSelf) == "MAINTENANCE" && Branch != "Dev")
            {
                Log.Info($"Update Server is currently under maintenance mode.");
                Log.Info($"Current branch is {Branch}");
                Log.Info("Now exiting...");
                Environment.Exit(0);
                return;
            }

            var newestVersion = await GetLatestVersionWithRetryAsync(ProjectInfoSelf);

            ProjectInfoSelf.ProjectNewVersion = newestVersion;
            
            var newVersion = Version.Parse(newestVersion);

            if (currentVersion == newVersion)
            {
                Log.Info("You are already using the latest version of CommonUpdater.");
                return;
            }

            if (currentVersion > newVersion)
            {
                Log.Info("You are using a testing version of CommonUpdater.");
                return;
            }

            await DownloadFileWithRetryAsync(ProjectInfoSelf);
            
            Log.Debug("Downloaded. Now extracting...");

            if (File.Exists(selfUpdatePath))
            {
                ExtractAndRunUpdaterHelper(Process.GetCurrentProcess().Id, Path.GetTempPath(), true);
                Environment.Exit(0);
            }
        }
    }
}