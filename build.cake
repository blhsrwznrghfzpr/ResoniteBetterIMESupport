var solutionPath = "./ResoniteBetterIMESupport.sln";
var propsPath = "./Directory.Build.props";

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

var packageVersion = XmlPeek(propsPath, "/Project/PropertyGroup/Version");
var distDir = Directory("./dist");

Task("Clean")
    .Does(() =>
{
    if (DirectoryExists(distDir))
    {
        CleanDirectory(distDir);
    }
    else
    {
        CreateDirectory(distDir);
    }
});

Task("BuildPlugin")
    .IsDependentOn("Clean")
    .Does(() =>
{
    Information($"Building ResoniteBetterIMESupport version {packageVersion}...");

    DotNetBuild(solutionPath, new DotNetBuildSettings
    {
        Configuration = configuration,
        Verbosity = DotNetVerbosity.Minimal
    });

    Information("ResoniteBetterIMESupport build completed successfully.");
});

Task("Build")
    .IsDependentOn("BuildPlugin")
    .Does(() =>
{
    Information($"Building Thunderstore package with version {packageVersion}...");

    var exitCode = StartProcess("dotnet", new ProcessSettings
    {
        Arguments = $"tcli build --package-version {packageVersion}",
        WorkingDirectory = Directory(".")
    });

    if (exitCode != 0)
    {
        throw new Exception($"dotnet tcli build failed with exit code {exitCode}");
    }

    Information("Thunderstore package build completed successfully.");
});

Task("Default")
    .IsDependentOn("Build");

RunTarget(target);
