using System.Text;

namespace CommonUpdater
{
    public class ProjectInfo
    {
        public string ProjectName { get; set; } = String.Empty;
        public string ProjectExeName { get; set; } = String.Empty;
        public string ProjectAuthor { get; set; } = String.Empty;
        // Now I see that inserting a column into a struct is not straightforward.
        public string ProjectBranch { get; set; } = String.Empty;
        public string ProjectCurrentVersion { get; set; } = String.Empty;
        public string ProjectCurrentExePath { get; set; } = String.Empty;
        public string ProjectNewExePath { get; set; } = String.Empty;
        public string ProjectNewVersion { get; set; } = String.Empty;

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(ProjectName);
            sb.AppendLine(ProjectExeName);
            sb.AppendLine(ProjectAuthor);
            sb.AppendLine(ProjectCurrentVersion);
            sb.AppendLine(ProjectCurrentExePath);
            sb.AppendLine(ProjectNewExePath);
            return sb.ToString();
        }
    }
}