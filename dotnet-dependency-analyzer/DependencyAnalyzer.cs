using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotnetDependencyAnalyzer.NetCore.Licenses;
using DotnetDependencyAnalyzer.NetCore.PackageUtils;
using DotnetDependencyAnalyzer.NetCore.Vulnerabilities;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;
using System.Xml.Serialization;

namespace DotnetDependencyAnalyzer.NetCore
{
    public class DependencyAnalyzer
    {
        private static readonly string projectFile = "{0}.csproj";
        private static readonly string nugetPropsFile = "{0}/obj/{1}.csproj.nuget.g.props";
        private static string projectPath;
        private static string packagesRootPath;
        private static string projectAssetsPath;

        private static readonly string reportAPIUrl = "http://35.234.147.77/report";

        public static HttpClient Client { get; } = new HttpClient();

        public static void Main(string[] args)
        {
			projectPath = (args.Length == 0) ? "./" : args[0];
            if(Directory.GetFiles(projectPath, "*.csproj").Length == 0)
            {
                Console.WriteLine( args.Length==0 ? "Project not found in current directory" : "Project not found in the directory specified");
                return;
            }
            DateTime startTime = DateTime.UtcNow;
            Console.WriteLine("Plugin is running... ");
            string projectName = new DirectoryInfo(projectPath).Name;
            RetrieveNugetProperties(string.Format(nugetPropsFile, projectPath, projectName));
            Console.WriteLine($"Project Name: {projectName}");
            string projectFilePath = Path.Combine(projectPath, string.Format(projectFile, projectName));

            if (TryGetPolicy(out ProjectPolicy policy))
            {
                if (File.Exists(projectFilePath))
                {
                    List<NuGetPackage> packagesFound = PackageLoader.LoadPackages(projectFilePath, projectAssetsPath)
                                                    .Distinct()
                                                    .ToList();
                    List<Dependency> dependenciesEvaluated = ValidateProjectDependencies(packagesFound, policy).Result;
                    string report = GenerateReport(dependenciesEvaluated, projectName, policy);
                    Console.WriteLine("Produced report locally.");
                    StoreReport(report);
                    double seconds = (DateTime.UtcNow - startTime).TotalSeconds;
                    Console.WriteLine("Plugin execution time: " + seconds);
                }
                else
                {
                    Console.WriteLine($".csproj file not found in project {projectName}");
                }
            }
        }

        private static void RetrieveNugetProperties(string propertiesFileDir)
        {
            var serializer = new XmlSerializer(typeof(NuGetProperties));
            using (var stream = File.OpenRead(propertiesFileDir))
            {
                NuGetProperties props = (NuGetProperties)serializer.Deserialize(stream);
                packagesRootPath = props.PropertyGroup[0].NuGetPackageRoot.Value.Replace("$(UserProfile)", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                projectAssetsPath = props.PropertyGroup[0].ProjectAssetsFile.Value;
            }
        }

        private static bool TryGetPolicy(out ProjectPolicy policy)
        {
            string[] osdaFiles = Directory.GetFiles(projectPath, ".osda");
            if (osdaFiles.Length == 0)
            {
                Console.WriteLine("Canceled report. This project does not contain a policy file (.osda).");
                policy = null;
                return false;
            }
            policy = JsonConvert.DeserializeObject<ProjectPolicy>(File.ReadAllText(osdaFiles[0]));
            if (policy.Id == "")
            {
                Console.WriteLine("Canceled report. Project id not specified in policy.");
                return false;
            }
            if (policy.Name == "")
            {
                Console.WriteLine("Canceled report. Project name not specified in policy.");
                return false;
            }
            if (policy.Admin == "")
            {
                Console.WriteLine("Canceled report. Project administrator name not specified in policy.");
                return false;
            }
            return true;
        }

        private static async Task<List<Dependency>> ValidateProjectDependencies(List<NuGetPackage> packages, ProjectPolicy policy)
        {
            List<Dependency> dependencies = new List<Dependency>();

            VulnerabilityEvaluationResult[] vulnerabilityEvaluationResult = await VulnerabilityEvaluation.EvaluatePackage(packages, policy.ApiCacheTime);

            List<License>[] dependenciesLicenses = new List<License>[packages.Count];
            int i = 0;
            foreach (NuGetPackage package in packages)
            {
                PackageInfo packageInfo = PackageManager.GetPackageInfo(packagesRootPath, package.Id, package.Version);
                dependencies.Add(new Dependency(packageInfo.Id, packageInfo.Version, packageInfo.Description, package.Direct, package.Children));
                try
                {
                    dependenciesLicenses[i] = await LicenseManager.TryGetLicenseName(packageInfo, policy.ApiCacheTime);
                }
                catch (Exception)
                {
                    dependenciesLicenses[i] = new List<License>();
                }
                ++i;
            }

            i = 0;
            foreach (List<License> licenses in dependenciesLicenses)
            {
                dependencies[i].Licenses = licenses.Select(license =>
                {
                    license.Valid = !policy.InvalidLicenses.Contains(license.Title);
                    return license;
                })
                    .ToList();
                dependencies[i].VulnerabilitiesCount = vulnerabilityEvaluationResult[i].VulnerabilitiesNumber;
                dependencies[i].Vulnerabilities = vulnerabilityEvaluationResult[i].VulnerabilitiesFound;
                ++i;
            }

            return dependencies;
        }

        private static string GenerateReport(List<Dependency> dependenciesEvaluated, string projectName, ProjectPolicy policy)
        {
            string dateTime = string.Concat(DateTime.UtcNow.ToString("s"), "Z");
            Report report = new Report(policy.Id, policy.Version, projectName, policy.Description, dateTime, policy.Organization, policy.Repo, policy.RepoOwner, policy.Admin)
            {
                Dependencies = dependenciesEvaluated
            };
            string jsonReport = JsonConvert.SerializeObject(report);
            File.WriteAllText(Path.Combine(projectPath, "report.json"), jsonReport);
            return jsonReport;
        }

        private static void StoreReport(string report)
        {
            var result = Client.PostAsync(reportAPIUrl, new StringContent(report, Encoding.UTF8, "application/json")).Result;
            if (result.IsSuccessStatusCode)
            {
                Console.WriteLine("Report stored with success");
            }
            else
            {
                Console.WriteLine("An error during report storage");
            }
        }
    }
}