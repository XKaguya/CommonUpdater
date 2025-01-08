using System.Diagnostics;
using System.Reflection;
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
        private static readonly string ServerUrl = "SERVER_ADDR";
        private static readonly string ProgramVersion = "1.0.5";
        private const int MaxRetryCount = 3;
        private static ProjectInfo _projectInfo = new();
        private static ProjectInfo _projectInfoSelf = new();

        public static async Task Main(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "CommonUpdater.log4net.config";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                XmlConfigurator.Configure(stream);
            }
            else
            {
                Log.Error($"Failed to find embedded resource: {resourceName}");
            }
            
            Log.Info($"CommonUpdater version: {ProgramVersion}");
            
            _projectInfo.ProjectName = args[0];
            _projectInfo.ProjectExeName = args[1];
            _projectInfo.ProjectAuthor = args[2];
            _projectInfo.ProjectCurrentVersion = args[3];
            _projectInfo.ProjectCurrentExePath = args[4];
            _projectInfo.ProjectNewExePath = args[5];

            await SelfUpdateAsync();

            try
            {
                if (args.Length != 6)
                {
                    Log.Info("Usage: CommonUpdater <projectName> <projectExeName> <projectAuthor> <projectCurrentVersion> <projectCurrentExePath> <projectNewExePath>");
                    return;
                }

                Log.Debug(_projectInfo.ToString());

                var newestVersion = await GetLatestVersionWithRetryAsync(_projectInfo);
                var currentVersion = Version.Parse(_projectInfo.ProjectCurrentVersion);
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

                await DownloadFileWithRetryAsync(_projectInfo);

                KillExistingInstances(_projectInfo.ProjectExeName);

                string tempDir = Path.GetTempPath();
                ExtractAndRunUpdaterHelper(0, tempDir);

                Process.Start(_projectInfo.ProjectCurrentExePath);

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
            string version = await RetryAsync(() => GetLatestVersionFromServer(projectInfo))
                             ?? await RetryAsync(() => GetLatestReleaseTagAsync(projectInfo));
            return version ?? throw new Exception("Failed to get the latest version.");
        }

        private static async Task<string> GetLatestReleaseTagAsync(ProjectInfo projectInfo)
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

        private static async Task<string> GetLatestVersionFromServer(ProjectInfo projectInfo)
        {
            try
            {
                using HttpClient httpClient = new HttpClient();

                var url = $"{ServerUrl}/Versions.json";

                string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo?.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

                HttpResponseMessage response = await httpClient.GetAsync(url);

                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();

                var jsonData = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonResponse);

                if (jsonData != null && jsonData.TryGetValue(projectInfo.ProjectName, out string? version))
                {
                    Log.Info($"Project Name: {projectInfo.ProjectName}");
                    Log.Info($"Newest Version on Server: {version}");
                    return version;
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

                string url =
                    $"https://github.com/{projectInfo.ProjectAuthor}/{projectInfo.ProjectName}/releases/latest/download/{projectInfo.ProjectExeName}";
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

                string url = $"{ServerUrl}/{projectInfo.ProjectName}/{projectInfo.ProjectExeName}";
                using HttpClient httpClient = new HttpClient();
                string userAgent =
                    $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo?.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

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
                
                var yaml = SerializeToYaml(_projectInfo);
                var processStartInfo = new ProcessStartInfo();

                if (isFromSelfUpdate)
                {
                    processStartInfo.FileName = helperPath;
                    processStartInfo.Arguments = $"\"{pid}\" \"{_projectInfoSelf.ProjectCurrentExePath}\" \"{_projectInfoSelf.ProjectNewExePath}\" \"{yaml}\" \"{isFromSelfUpdate.ToString()}\"";
                    processStartInfo.UseShellExecute = false;
                }
                else
                {
                    processStartInfo.FileName = helperPath;
                    processStartInfo.Arguments = $"\"{pid}\" \"{_projectInfo.ProjectCurrentExePath}\" \"{_projectInfo.ProjectNewExePath}\" \"{yaml}\" \"{isFromSelfUpdate.ToString()}\"";
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
            _projectInfoSelf.ProjectName = "CommonUpdater";
            _projectInfoSelf.ProjectCurrentVersion = ProgramVersion;
            _projectInfoSelf.ProjectAuthor = "XKaguya";
            _projectInfoSelf.ProjectExeName = "CommonUpdater.exe";
            _projectInfoSelf.ProjectCurrentExePath = selfUpdatePathOld;
            _projectInfoSelf.ProjectNewExePath = selfUpdatePath;

            Log.Debug(_projectInfoSelf.ToString());

            var newestVersion = await GetLatestVersionWithRetryAsync(_projectInfoSelf);
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

            await DownloadFileWithRetryAsync(_projectInfoSelf);

            if (File.Exists(selfUpdatePath))
            {
                ExtractAndRunUpdaterHelper(Process.GetCurrentProcess().Id, Path.GetTempPath(), true);
                Environment.Exit(0);
            }
        }
    }
}