using Corney.Common.AppVersion;
using Corney.Common.SimpleEnv;

namespace Corney.Features.App;

public class CorneyRegistry
{
    public CorneyRegistry(SimpleEnvResult env, string configPath, CorneyConfig config)
    {
        ConfigFilePath = configPath;
        CrontabFiles = config.CrontabFiles;
        AppVersion = AssemblyVersionParser.FromCurrentApp();
        AppEnv = env;
    }

    public SimpleEnvResult AppEnv { get; set; }
    public AssemblyVersionInfo AppVersion { get; }  
    public string ConfigFilePath { get; }
    public string[] CrontabFiles { get; }
}