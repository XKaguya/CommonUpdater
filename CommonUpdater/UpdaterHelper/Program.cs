using System.Diagnostics;
using System.Reflection;
using log4net;
using log4net.Config;
using YamlDotNet.Serialization;

namespace UpdaterHelper
{
    class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));
        static void Main(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "UpdaterHelper.log4net.config";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                XmlConfigurator.Configure(stream);
            }
            else
            {
                Log.Error($"Failed to find embedded resource: {resourceName}");
            }

            Log.Debug("Log4net initialized successfully using embedded configuration.");
            
            string parentProcessIdStr = args[0];
            string targetExePath = args[1];
            string newExePath = args[2];    
            string projectInfoYaml = args[3];
            bool isFromSelfUpdateBool = bool.Parse(args[4]);
            ProjectInfo projectInfo = DeserializeFromYaml(projectInfoYaml);

            if (!int.TryParse(parentProcessIdStr, out int parentProcessId))
            {
                Log.Error("Invalid parent process ID.");
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
                
                if (File.Exists(newExePath))
                {
                    File.Delete(newExePath);
                }

                if (isFromSelfUpdateBool)
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = targetExePath,
                        Arguments = $"\"{projectInfo.ProjectName}\" \"{projectInfo.ProjectExeName}\" \"{projectInfo.ProjectAuthor}\" \"{projectInfo.ProjectCurrentVersion}\" \"{projectInfo.ProjectCurrentExePath}\" \"{projectInfo.ProjectNewExePath}\"",
                        UseShellExecute = false
                    };
                    
                    Process.Start(processStartInfo);
                }
                else
                {
                    Process.Start(targetExePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Update failed: {ex.Message}");
            }
        }
        
        private static ProjectInfo DeserializeFromYaml(string yaml)
        {
            var deserializer = new DeserializerBuilder().Build();
            return deserializer.Deserialize<ProjectInfo>(yaml);
        }
    }
}