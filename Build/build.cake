#module nuget:?package=Cake.DotNetTool.Module&version=0.3.0         // dotnet tool nuget package loader - needs bootstrap - see build.ps1 at the end
#addin nuget:?package=Cake.Http&version=1.3.0
#addin nuget:?package=Newtonsoft.Json&version=13.0.1
#addin nuget:?package=Cake.FileHelpers&version=5.0.0
#addin nuget:?package=Cake.Sonar&version=1.1.30
#addin nuget:?package=Cake.Coverlet&version=2.3.4
#addin nuget:?package=ReedExpo.Cake.Coverlet&Version=1.0.7
#tool nuget:?package=MSBuild.SonarQube.Runner.Tool&version=4.8.0
#tool nuget:?package=NUnit.ConsoleRunner&version=3.11.1
#tool nuget:?package=NUnit.Extension.NUnitV2ResultWriter&version=3.6.0
#tool  dotnet:?package=coverlet.console&version=1.7.2
#tool dotnet:?package=CycloneDX&version=2.3.0                    // will be installed at .\tools\dotnet-CycloneDX.exe

using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var appName = Argument<string>("appName", "reedexpo.digital.exbox");
var rebuild = Argument("rebuild", true);
var buildNumber = Argument("buildNumber", "buildNumber");
var createArtifact = Argument("createArtifact", false);
var integrationTests= Argument("integrationTests", true);
var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var buildmode = Argument("buildmode", "default");
var isCI = buildmode == "CI";
var runSonarQube = isCI && string.IsNullOrEmpty(EnvironmentVariable("bamboo_SKIP_SONARQUBE"));

// Assumes your test projects are in this folder relative to build.cake. Change if needed
var testDir = Directory("../test");
var testResultsDir =   MakeAbsolute(testDir + Directory("TestResults"));
var coverageResultsDir = MakeAbsolute(testDir + Directory("CoverageResults"));

// For coverlet, we need to pre-determine all the opencover output files.
// This list is used to select all the test projects we intend to run here
var unitTestProjects = new List<string> {
     "Reedexpo.Digital.Exbox.Unit.Test",
     "Reedexpo.Digital.Exbox.Repository.Integration.Test"
    // <- List your unit test projects here.
};

var coverletHelper = GetCoverletHelper(unitTestProjects, testDir, coverageResultsDir);

Dictionary<string, Object> config;
var postgresPasswordFile = "../postgres.txt";
var nugetConfigFile = "../NuGet.config";
var buildDir = Directory(".");
var deployDir = Directory("../Deploy");
var sourceRoot = Directory("../src");

var buildOutputDir = Directory("../output");
var artifactDir = buildOutputDir + Directory("artifact");

var loggersArgument = "--logger:ReportPortal --logger:trx";
var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
var appBundleZip = File("bundle.zip");
var cyclonePath = GetFiles($"./**/dotnet-CycloneDX{(isWindows ? ".exe" : string.Empty)}").FirstOrDefault();
FilePath SolutionFile = new FilePath("Reedexpo.Digital.Exbox.sln").MakeAbsolute(System.IO.Path.Combine(Context.Environment.WorkingDirectory.FullPath, @"..\"));

if (cyclonePath == null)
{
    throw new CakeException("Can't find CycloneDX tool");
}
else
{
    Information($"Found CycloneDX: {cyclonePath}");
}

var tempWebServiceDir = buildOutputDir + Directory("temp-webService");

var isBamboo = Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("bamboo_", StringComparison.OrdinalIgnoreCase));

Verbose("Creating Artifact: " + createArtifact);
Verbose("Building on " + (isWindows ? "Windows" : "Linux"));
//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .WithCriteria(rebuild)
    .Does(() =>
{
    CleanDirectories("../src/**/bin/" + configuration);
    CleanDirectories(testResultsDir.ToString());
    CleanDirectories(coverageResultsDir.ToString());
    CleanDirectory(buildOutputDir);
});

// Task("NuGetConfig")
//     .WithCriteria(isBamboo)
//     .Does(() =>
// {
//     var progetUsername = EnvironmentVariableStrict("bamboo_ATLAS_PROGET_USERNAME");
//     var progetPassword = EnvironmentVariableStrict("bamboo_ATLAS_PROGET_PASSWORD");
//     var progetUb = new UriBuilder(EnvironmentVariableStrict("bamboo_PROGET_URL"));
//
//     progetUb.Path = "/nuget/Default/";
//     CopyFile("../nuget.config.template", nugetConfigFile);
//
//     ReplaceTextInFiles(nugetConfigFile, "{PROGET_USERNAME}", progetUsername);
//     ReplaceTextInFiles(nugetConfigFile, "{PROGET_PASSWORD}", progetPassword);
//     ReplaceTextInFiles(nugetConfigFile, "{PROGET_URL}", progetUb.Uri.AbsoluteUri);
// });

Task("RestoreNuGetPackages")
    .WithCriteria(rebuild)
    .IsDependentOn("Clean")
    .Does(() =>
{
         DotNetRestore("../");
});

Task("Build")
    .IsDependentOn("RestoreNuGetPackages")
    .Does(() =>
{
    if (isWindows)
    {
        var buildSettings= new DotNetMSBuildSettings();
            DotNetBuild(SolutionFile.FullPath, new DotNetBuildSettings {
                Configuration = configuration,
                MSBuildSettings = buildSettings
            });
    }
    else
    {
        var args = "build ../Reedexpo.Digital.Exbox.sln -c " + configuration;
        IEnumerable<string> redirectedStandardOutput;
        IEnumerable<string> redirectedErrorOutput;
        var resultCode = StartProcess("dotnet",
            new ProcessSettings { Arguments = args, RedirectStandardError = true, RedirectStandardOutput = true },
            out redirectedStandardOutput,
            out redirectedErrorOutput
            );

        if (resultCode != 0)
        {
            foreach (var line in redirectedErrorOutput)
            {
                Error(line);
            }

            throw new Exception("dotnet exited with " + resultCode);
        }
        else
        {
            foreach (var line in redirectedStandardOutput)
            {
                Information(line);
            }
        }
    }
});

Task("TestConfig")
  .Does(() => {
	EnsureDirectoryExists(testResultsDir);
    EnsureDirectoryExists(coverageResultsDir);
    SetupEnvironment("localtest");
	WriteAppSettingsToFile();
    RenderTemplate("../src/Reedexpo.Digital.Exbox/Database/dbdeploy.postgres.config.xml");
    RenderTemplate("../Deploy/DBCreator.Postgres/AppSettings.config");
});

Task("RequestPostgresPassword")
    .WithCriteria(!FileExists(postgresPasswordFile))
    .Does(() =>
{
    var postgresPassword = Prompt("Enter your Postgres pasword: ");
    FileWriteLines(postgresPasswordFile, new[]{postgresPassword});
});

Task("SetUpPgDb")
    .Does(() =>
{
    StartProcess($"../Deploy/DBCreator.Postgres/DbCreator.Postgres{(isWindows ? ".exe" : string.Empty)}");
    StartProcess($"../src/Reedexpo.Digital.Exbox/Database/dbdeploy.net/dbdeploy{(isWindows ? ".exe" : string.Empty)}", new ProcessSettings {
        Arguments = new ProcessArgumentBuilder().Append("--config=../src/Reedexpo.Digital.Exbox/Database/dbdeploy.postgres.config.xml")
    });
});

// Task("GenerateTestConfigFiles")
//     .Does(() =>
// {
//     RenderTemplate("../ExboxApiTest/appSettings.config");
//     RenderTemplate("../ExboxService/Web.appSettings.config");
// });

Task("ReportPortalConfig")
    .Does(() =>
{
    var reportPortalConfigAPI = "../test/Reedexpo.Digital.Exbox.API.Test/ReportPortal.config.json";
    var reportPortalConfigRepoInt = "../test/Reedexpo.Digital.Exbox.Repository.Integration.Test/ReportPortal.config.json";
    var reportPortalConfigUnit = "../test/Reedexpo.Digital.Exbox.Unit.Test/ReportPortal.config.json";
    var reportPortalUrl = EnvironmentVariableStrict("bamboo_REPORTPORTAL_URL");
    var uuid = EnvironmentVariableStrict("bamboo_REPORTPORTAL_UUID");

    CopyFile("../test/Reedexpo.Digital.Exbox.API.Test/ReportPortal.config.json.template", reportPortalConfigAPI);
    ReplaceTextInFiles(reportPortalConfigAPI, "{bamboo_REPORTPORTAL_URL}", reportPortalUrl);
    ReplaceTextInFiles(reportPortalConfigAPI, "{bamboo_REPORTPORTAL_UUID}", uuid);

    CopyFile("../test/Reedexpo.Digital.Exbox.Repository.Integration.Test/ReportPortal.config.json.template", reportPortalConfigRepoInt);
    ReplaceTextInFiles(reportPortalConfigRepoInt, "{bamboo_REPORTPORTAL_URL}", reportPortalUrl);
    ReplaceTextInFiles(reportPortalConfigRepoInt, "{bamboo_REPORTPORTAL_UUID}", uuid);

    CopyFile("../test/Reedexpo.Digital.Exbox.Unit.Test/ReportPortal.config.json.template", reportPortalConfigUnit);
    ReplaceTextInFiles(reportPortalConfigUnit, "{bamboo_REPORTPORTAL_URL}", reportPortalUrl);
    ReplaceTextInFiles(reportPortalConfigUnit, "{bamboo_REPORTPORTAL_UUID}", uuid);
});

Task("UnitTests")
    .IsDependentOn("TestConfig")   // your own dependencies or criteria
	.Does(() => {
        foreach(var testProjectPath in coverletHelper.GetTestProjectFilePaths())
        {
            DotNetTest(testProjectPath.FullPath, new DotNetTestSettings {
                Configuration = "Debug",   // <- IMPORTANT
                ArgumentCustomization = args => args.Append (loggersArgument)
            });

            Coverlet(testProjectPath, new CoverletSettings {
                    CollectCoverage = true,
                    CoverletOutputFormat = CoverletOutputFormat.opencover,
                    CoverletOutputDirectory = coverageResultsDir,
                    CoverletOutputName = coverletHelper.GetCoverageOutputFilenameForProject(testProjectPath),
                    Exclude = new List<string> {
                        "[Reedexpo.Digital.Exbox.Unit.Test.*]",
                        "[Reedexpo.Digital.Exbox.Repository.Integration.Test.*]"
                    }
            });
        }
});


Task("APITests")
    .IsDependentOn("TestConfig")
	.Does(() => {
        // Argument customisation may not work with future versions of dotnet.exe as the -xml
        // is an XUnit specific switch.
        // See https://github.com/dotnet/cli/issues/4921
        // Using XUnit2 command failed because it cannot find xunit.dll alongside the test dll.
        DotNetTest(System.IO.Path.GetFullPath("./../test/Reedexpo.Digital.Exbox.API.Test/Reedexpo.Digital.Exbox.API.Test.csproj"), new DotNetTestSettings {
            Configuration = configuration,
            WorkingDirectory = testResultsDir,
            ArgumentCustomization = args => args.Append(loggersArgument)
        });
});

Task("CleanArtifacts")
    .WithCriteria(createArtifact)
    .Does(() =>
{
});

Task("AddWebServiceToArtifact")
    .WithCriteria(createArtifact)
    .Does(() =>
{
    DotNetPublish("../src/Reedexpo.Digital.Exbox", new DotNetPublishSettings
        {
            Configuration = configuration,
            OutputDirectory = tempWebServiceDir
        });

    //http://docs.aws.amazon.com/elasticbeanstalk/latest/dg/dotnet-manifest.html
    EnsureDirectoryExists(artifactDir);
    CopyFile(nugetConfigFile, artifactDir + File("NuGet.config"));
    CopyFile(buildDir + File("env.yaml"), tempWebServiceDir + File("env.yaml"));
    CopyFile(buildDir + File("aws-windows-deployment-manifest.json"), tempWebServiceDir + File("aws-windows-deployment-manifest.json"));
    CopyDirectory(buildDir + Directory("../src/Reedexpo.Digital.Exbox/Database"), tempWebServiceDir + Directory("Database"));
    Zip(tempWebServiceDir , artifactDir + appBundleZip);
});

Task("AddDeploymentScriptsToArtifact")
    .WithCriteria(createArtifact)
    .Does(() =>
    {
        CopyDirectory(deployDir, artifactDir);
    });

Task("CreateBillOfMaterials")
    .WithCriteria(isCI)
    .Does(() => {
        EnsureDirectoryExists(artifactDir);

        DotNetTool("../Reedexpo.Digital.Exbox.sln", new DotNetToolSettings {
            ToolPath = cyclonePath,
            ArgumentCustomization = args => args.Append($" -o {artifactDir}")
            }
        );
    });

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("CreateArtifact")
    .WithCriteria(createArtifact)
    .IsDependentOn("AddWebServiceToArtifact")
    .IsDependentOn("AddDeploymentScriptsToArtifact");

Task("Default")
    .IsDependentOn("Clean")
    .IsDependentOn("SonarBegin")
    .IsDependentOn("Build")
    .IsDependentOn("TestConfig")
	.IsDependentOn("SetUpPgDb")
    .IsDependentOn("ReportPortalConfig")
    .IsDependentOn("UnitTests")
    .IsDependentOn("APITests")
    .IsDependentOn("CreateBillOfMaterials")
    .IsDependentOn("SonarEnd")
    .IsDependentOn("CreateArtifact");

Task("LocalBuild")
    .IsDependentOn("Clean")
    .IsDependentOn("Build")
    .IsDependentOn("CreateArtifact");

 Task("SonarBegin")
  .WithCriteria(isCI)
  .Does(() => {
   });

Task("SonarEnd")
  .WithCriteria(runSonarQube)
  .Does(() => {
    });

 Task("MigrateLocal")
	.IsDependentOn("RestoreNuGetPackages")
    .IsDependentOn("GenerateLocalConfigFiles")
    .IsDependentOn("SetUpPgDb");

 Task("MigrateTest")
	.IsDependentOn("RestoreNuGetPackages")
    .IsDependentOn("GenerateTestConfigFiles")
    .IsDependentOn("SetUpPgDb");


Task("GenerateLocalConfigFiles")
    .IsDependentOn("RequestPostgresPassword")
    .Does(() =>
{
    GeneratePGConfigFiles("local");
});


Task("GenerateTestConfigFiles")
    .IsDependentOn("RequestPostgresPassword")
    .Does(() =>
{
    GeneratePGConfigFiles("localtest");
});

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);

void GeneratePGConfigFiles(string env){
    SetupEnvironment(env);
    RenderTemplate("../src/Reedexpo.Digital.Exbox/Database/dbdeploy.postgres.config.xml");
}

string EnvironmentVariableStrict(string key)
{
    var value = EnvironmentVariable(key);

    if (value == null)
    {
        throw new Exception("Environment Variable not found: " + key);
    }
    return value;
}

string EnvironmentVariableOrDefault(string key, string defaultValue)
{
    var value = EnvironmentVariable(key);

    return value == null ? defaultValue : value;
}

string Prompt(string prompt)
{
    if (string.IsNullOrEmpty(EnvironmentVariable("bamboo_buildKey")))
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }
    else
    {
        throw new CakeException($"Cannot prompt for '{prompt}' on a build server.");
    }
}

// Migrated from Exbox .Net framework setup
string GenerateDbConnectionString(string username, string password, string dbName)
{
    var baseConnectionString = "Host=" + config["databaseUrl"] + ";Database=" + dbName + ";";
    return baseConnectionString + "Username=" + username + ";Password=" + password + ";";
}

void WriteAppSettingsToFile()
{
    var serialized = JsonConvert.SerializeObject(config, Formatting.Indented);
	var appSettingsPath = File("../test/Reedexpo.Digital.Exbox.Repository.Integration.Test/testSettings.json");
    FileWriteText(appSettingsPath, serialized);
	var apiSettingsPath = File("../test/Reedexpo.Digital.Exbox.API.Test/appsettings.json");
    FileWriteText(apiSettingsPath, serialized);
}

void RenderTemplate(string path)
{
    var transform = TransformTextFile($"{path}.t", "{", "}");

    foreach(KeyValuePair<string, Object> pair in config)
    {
        transform.WithToken(pair.Key, pair.Value);
    }

    transform.Save(path);
}

void SetupEnvironment(string environment) {
    Information(environment);
    Information("xxxxxxxxxxxxxxxxx");
    var text = FileReadText($"../Deploy/config/{environment}/environment.json");
    config = Newtonsoft.Json.Linq.JObject.Parse(text).ToObject<Dictionary<string, Object>>();

    if ((environment == "local" || environment == "localtest")
        && FileExists(postgresPasswordFile))
    {
        var postgresFileLines = FileReadLines(postgresPasswordFile);
        config["databaseMasterPassword"] = postgresFileLines[0];
    }
    config["masterDbConnectionString"] = GenerateDbConnectionString(config["databaseMasterUsername"].ToString(), config["databaseMasterPassword"].ToString(), config["dbName"].ToString());
    config["dbCreatorPostgresConnectionString"] = GenerateDbConnectionString(config["databaseMasterUsername"].ToString(), config["databaseMasterPassword"].ToString(), "postgres");
    config["exboxDbConnectionString"] = GenerateDbConnectionString(config["dbUsername"].ToString(), config["dbPassword"].ToString(), config["dbName"].ToString());
}
