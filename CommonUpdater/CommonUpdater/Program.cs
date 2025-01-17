using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using CommonUpdater.Native;
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
        private const string ServerUrl = "SERVER_ADDR";
        private const string ProgramVersion = "1.0.8";
        private const int MaxRetryCount = 3;
        private static readonly ProjectInfo ProjectInfo = new();
        private static readonly ProjectInfo ProjectInfoSelf = new();
        
        private static readonly Dictionary<string, ProjectEntry> ProjectEntries = new();

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
            
            if (args.Length != 6)
            {
                Log.Info("Usage: CommonUpdater <projectName> <projectExeName> <projectAuthor> <projectCurrentVersion> <projectCurrentExePath> <projectNewExePath>");
                return;
            }
            
            ProjectInfo.ProjectName = args[0];
            ProjectInfo.ProjectExeName = args[1];
            ProjectInfo.ProjectAuthor = args[2];
            ProjectInfo.ProjectCurrentVersion = args[3];
            ProjectInfo.ProjectCurrentExePath = args[4];
            ProjectInfo.ProjectNewExePath = args[5];

            if (Log.IsDebugEnabled)
            {
                Log.Debug(string.Join(", ", args.Select(arg => arg?.ToString() ?? "null")));
            }

            await SelfUpdateAsync();

            try
            {
                var (statusCode, newestVersion) = await GetLatestVersionWithRetryAsync(ProjectInfo);
                
                if (IsServerUnderMaintenanceMode() && Branch != "Dev")
                {
                    Log.Info($"Update Server is currently under maintenance mode.");
                    Log.Info($"Current branch is {Branch}");
                    Log.Info("Now exiting...");
                    Environment.Exit(0);
                    return;
                }
                
                ProjectInfo.ProjectNewVersion = newestVersion;
                
                var currentVersion = Version.Parse(ProjectInfo.ProjectCurrentVersion);
                var newVersion = Version.Parse(newestVersion);

                if (currentVersion == newVersion && statusCode != StatusCode.ForceUpdate)
                {
                    Log.Info("You are already using the latest version.");
                    return;
                }

                if (currentVersion > newVersion && statusCode != StatusCode.ForceUpdate)
                {
                    Log.Info("You are using a testing version.");
                    return;
                }

                if (statusCode == StatusCode.ForceUpdate && currentVersion != newVersion)
                {
                    Log.Info("Force update branch");
                    await DownloadFileWithRetryAsync(ProjectInfo);
                    await PerformUpdateAsync(ProjectInfo);
                }
                if (statusCode == StatusCode.Update && currentVersion != newVersion)
                {
                    Log.Info("Normal update branch");
                    await DownloadFileWithRetryAsync(ProjectInfo);
                    await PerformUpdateAsync(ProjectInfo);
                }
                if (statusCode == StatusCode.NoUpdate || statusCode == StatusCode.ForceUpdate && newVersion == currentVersion)
                {
                    Log.Info($"No updates are available.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Update failed: {ex.Message}");
            }
        }

        private static bool IsServerUnderMaintenanceMode()
        {
            return ProjectEntries.ContainsKey("MaintenanceMode") && ProjectEntries["MaintenanceMode"].MaintenanceMode;
        }

        private static async Task<(StatusCode, string)> GetLatestVersionWithRetryAsync(ProjectInfo projectInfo)
        {
            var (status, version) = await RetryAsync(() => GetLatestVersionFromServer(projectInfo));
            
            Log.Debug($"Project {projectInfo.ProjectName}");
            Log.Debug($"Status code: {status.ToString()}");
            Log.Debug($"Current version is {projectInfo.ProjectCurrentVersion}");
            Log.Debug($"Latest version is {version}");
            
            if (string.IsNullOrEmpty(version) || status == StatusCode.Null)
            {
                version = await RetryAsync(() => GetLatestReleaseTagAsync(projectInfo));
            }

            return !string.IsNullOrEmpty(version) ? (status, version) : throw new Exception("Failed to get the latest version.");
        }

        private static async Task<string?> GetLatestReleaseTagAsync(ProjectInfo projectInfo)
        {
            try
            {
                string url = $"https://api.github.com/repos/{projectInfo.ProjectAuthor}/{projectInfo.ProjectName}/releases/latest";
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("request");

                var responseBody = await httpClient.GetStringAsync(url);
                var json = JObject.Parse(responseBody);

                var version = json["tag_name"]?.ToString();
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

        private static async Task<(StatusCode, string?)> GetLatestVersionFromServer(ProjectInfo projectInfo)
        {
            try
            {
                using HttpClient httpClient = new HttpClient();

                // For compatibility, Versions.json has been deprecated.
                var url = $"{ServerUrl}/Projects.json";

                string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

                HttpResponseMessage response = await httpClient.GetAsync(url);

                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                
                var projectConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonResponse);

                var maintenanceEntry = new ProjectEntry();
                maintenanceEntry.MaintenanceMode = Convert.ToBoolean(projectConfig["MaintenanceMode"]);
                if (!ProjectEntries.ContainsKey("MaintenanceMode"))
                {
                    ProjectEntries.Add("MaintenanceMode", maintenanceEntry);
                }
            
                foreach (var project in projectConfig)
                {
                    if (project.Key != "MaintenanceMode" && !ProjectEntries.ContainsKey(project.Key))
                    {
                        var projectDetails = project.Value.ToString();
                        var details = JsonConvert.DeserializeObject<Dictionary<string, object>>(projectDetails);
                    
                        string version = details["Version"].ToString();
                        bool forceUpdate = Convert.ToBoolean(details["ForceUpdate"]);
                    
                        ProjectEntries.Add(project.Key, new ProjectEntry
                        {
                            ProjectName = project.Key,
                            ProjectCurrentVersion = projectInfo.ProjectCurrentVersion,
                            ProjectVersion = version,
                            ProjectForceUpdate = forceUpdate
                        });
                    }
                }

                if (Log.IsDebugEnabled)
                {
                    foreach (var kvp in ProjectEntries)
                    {
                        if (kvp.Key == "MaintenanceMode")
                        {
                            Log.Debug($"MaintenanceMode: {kvp.Value.MaintenanceMode}");
                        }
                        else
                        {
                            Log.Debug($"Project: {kvp.Value.ProjectName}, Version: {kvp.Value.ProjectVersion}, Force Update: {kvp.Value.ProjectForceUpdate}");
                        }
                    }
                }
                
                string projectName = Branch == "Dev" ? $"{projectInfo.ProjectName}Dev" : projectInfo.ProjectName;

                if (ProjectEntries[projectName] != null)
                {
                    Version currentVersion = Version.Parse(ProjectEntries[projectName].ProjectCurrentVersion);
                    Version projectVersion = Version.Parse(ProjectEntries[projectName].ProjectVersion);
                    
                    if (projectVersion != currentVersion && ProjectEntries[projectName].ProjectForceUpdate)
                    {
                        return (StatusCode.ForceUpdate, ProjectEntries[projectName].ProjectVersion);
                    }
                    if (projectVersion != currentVersion)
                    {
                        return (StatusCode.Update, ProjectEntries[projectName].ProjectVersion);
                    }
                    if (projectVersion == currentVersion)
                    {
                        return (StatusCode.NoUpdate, ProjectEntries[projectName].ProjectVersion);
                    }
                }
                
                return (StatusCode.Null, null);
            }
            catch (Exception ex)
            {
                Log.Error($"Error fetching version from server: {ex.Message}");
                return (StatusCode.Null, null);
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

                Log.Info($"Downloading the newest exe");

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
                    File.Delete(projectInfo.ProjectNewExePath);

                string url = Branch == "Dev" ? $"{ServerUrl}/{projectInfo.ProjectName}Dev/{projectInfo.ProjectExeName}" : $"{ServerUrl}/{projectInfo.ProjectName}/{projectInfo.ProjectExeName}";
                string patchNoteUrl = Branch == "Dev" ? $"{ServerUrl}/{projectInfo.ProjectName}Dev/PatchNote-{projectInfo.ProjectNewVersion}.txt" : $"{ServerUrl}/{projectInfo.ProjectName}/PatchNote-{projectInfo.ProjectNewVersion}.txt";
                
                using var httpClient = new HttpClient();
                string userAgent = $"CommonUpdater-{(string.IsNullOrEmpty(projectInfo.ProjectName) ? "Null" : projectInfo.ProjectName)}-{(string.IsNullOrEmpty(projectInfo?.ProjectCurrentVersion) ? "Null" : projectInfo.ProjectCurrentVersion)}";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                Log.Info($"Downloading the newest exe");

                await using var stream = await httpClient.GetStreamAsync(url);
                await using var fileStream = new FileStream(projectInfo.ProjectNewExePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);

                Log.Info("Downloading the patch note...");

                try
                {
                    var patchNoteContent = await httpClient.GetStringAsync(patchNoteUrl);
                    DisplayPatchNote(patchNoteContent);
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Info("No patch note.");
                }

                Log.Info("Download successfully.");
            }
            catch (Exception ex)
            {
                Log.Error($"Error downloading file from server: {ex.Message}");
            }
        }
        
        private static void DisplayPatchNote(string patchNoteContent)
        {
            var stringBuilder = new StringBuilder();
            var lines = patchNoteContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            stringBuilder.AppendLine("Update Available");
            stringBuilder.AppendLine("Patch Note:");

            foreach (var line in lines)
            {
                string trimmedLine = line.Trim();
                if (!string.IsNullOrEmpty(trimmedLine))
                {
                    stringBuilder.AppendLine(trimmedLine);
                }
            }

            MessageBox.Show(stringBuilder.ToString(), "PatchNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        
        private static async Task<(StatusCode, string)> RetryAsync(Func<Task<(StatusCode, string)>> operation)
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

            return (StatusCode.Null, string.Empty);
        }

        private static async Task PerformUpdateAsync(ProjectInfo projectInfo)
        {
            KillExistingInstances(projectInfo.ProjectExeName);

            string tempDir = Path.GetTempPath();
            ExtractAndRunUpdaterHelper(0, tempDir);

            Process.Start(projectInfo.ProjectCurrentExePath);

            Log.Info("Project update successfully.");
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

            var (statusCode, newestVersion) = await GetLatestVersionWithRetryAsync(ProjectInfoSelf);
            
            if (IsServerUnderMaintenanceMode() && Branch != "Dev")
            {
                Log.Info($"Update Server is currently under maintenance mode.");
                Log.Info($"Current branch is {Branch}");
                Log.Info("Now exiting...");
                Environment.Exit(0);
                return;
            }

            ProjectInfoSelf.ProjectNewVersion = newestVersion;
            
            var newVersion = Version.Parse(newestVersion);

            if (currentVersion == newVersion && statusCode != StatusCode.ForceUpdate)
            {
                Log.Info("You are already using the latest version of CommonUpdater.");
                return;
            }

            if (currentVersion > newVersion && statusCode != StatusCode.ForceUpdate)
            {
                Log.Info("You are using a testing version of CommonUpdater.");
                return;
            }

            if (statusCode == StatusCode.ForceUpdate && currentVersion != newVersion)
            {
                await DownloadFileWithRetryAsync(ProjectInfoSelf);
            
                Log.Debug("Downloaded. Now extracting...");

                if (File.Exists(selfUpdatePath))
                {
                    Log.Debug("Executing self update with code: ForceUpdate");
                    ExtractAndRunUpdaterHelper(Process.GetCurrentProcess().Id, Path.GetTempPath(), true);
                    Environment.Exit(0);
                }
            }

            if (statusCode == StatusCode.Update)
            {
                await DownloadFileWithRetryAsync(ProjectInfoSelf);
            
                Log.Debug("Downloaded. Now extracting...");

                if (File.Exists(selfUpdatePath))
                {
                    Log.Debug("Executing self update with code: Update");
                    ExtractAndRunUpdaterHelper(Process.GetCurrentProcess().Id, Path.GetTempPath(), true);
                    Environment.Exit(0);
                }
            }
        }
    }
}