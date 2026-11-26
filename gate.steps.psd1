@{
    Schema   = 1
    Repo     = 'Manuals'
    Workflow = '.github/workflows/main_crgolden-manuals.yml'
    Sonar    = @{ ProjectKey = 'crgolden_Manuals' }
    Probe    = @{
        Kind        = 'dotnet'
        Projects    = @('Manuals.Tests.Unit', 'Manuals.Tests.Integration')
        Environment = @{ 'Manuals.Tests.Integration' = @{ ASPNETCORE_ENVIRONMENT = 'Development'; RedisDatabase = '1' } }
        Wrap        = @{ 'Manuals.Tests.Integration' = @('pwsh', '-NoProfile', '-File', 'scripts/gate/Use-IntegrationRedis.ps1') }
    }
    Steps    = @(
        @{ Name = 'Regenerate lock files and push'; Delivery = $true }
        @{ Name = 'Install SonarQube Cloud scanner'; Tool = 'dotnet-sonarscanner'; Inputs = @('*') }
        @{ Name = 'Install dotnet-coverage'; Tool = 'dotnet-coverage'; Inputs = @('*') }
        @{
            Name   = 'Begin Sonar analysis'
            Sonar  = 'begin'
            Needs  = @('Install SonarQube Cloud scanner')
            Inputs = @('*')
            Run    = @(
                'dotnet-sonarscanner', 'begin', '/k:{sonarProject}', '/o:crgolden', '/d:sonar.host.url=https://sonarcloud.io',
                '/d:sonar.cs.opencover.reportsPaths=coverage.opencover.xml', '/d:sonar.cs.vscoveragexml.reportsPaths=coverage-integration.xml',
                '/d:sonar.exclusions=**/bin/**,**/obj/**', '/d:sonar.coverage.exclusions=**/Program.cs,**/gate.ps1,**/gate.steps.psd1,scripts/gate/**',
                '/d:sonar.qualitygate.wait=true', '/d:sonar.scanner.skipJreProvisioning=true',
                '/d:sonar.scm.use.blame.algorithm=GIT_NATIVE_BLAME', '/d:sonar.branch.name={sonarBranch}'
            )
        }
        @{
            Name   = 'Build with dotnet'
            Sonar  = 'build'
            Inputs = @('*')
            Run    = @('dotnet', 'build', '--no-incremental', '--configuration', 'Release', '/p:RestoreLockedMode=true', '-warnaserror')
        }
        @{ Name = 'Restore local tools'; Run = @('dotnet', 'tool', 'restore'); Inputs = @('dotnet-tools.json'); Always = $true }
        @{
            Name   = 'ReSharper inspection (jb inspectcode)'
            Needs  = @('Build with dotnet', 'Restore local tools')
            Inputs = @('*')
            Remove = @('{output}\manuals-inspect.sarif')
            Run    = @('dotnet', 'jb', 'inspectcode', 'Manuals.slnx', '--no-build', '-e=WARNING', '--caches-home={inspectCache}', '--output={output}\manuals-inspect.sarif')
            Check  = @{ Sarif = '{output}\manuals-inspect.sarif' }
        }
        @{
            Name   = 'Run unit tests with coverage'
            Needs  = @('Build with dotnet', 'Restore local tools')
            Inputs = @('*', '!Manuals.Tests.Integration/*')
            Remove = @('Manuals.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx')
            Run    = @(
                'dotnet', 'coverlet', 'Manuals.Tests.Unit/bin/Release/net11.0', '--target', 'dotnet',
                '--targetargs', 'test --project Manuals.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Manuals.Tests.Unit/bin/Release/net11.0/TestResults',
                '--format', 'opencover', '--output', 'coverage.opencover.xml', '--skipautoprops',
                '--exclude-by-attribute', 'GeneratedCodeAttribute', '--exclude-by-file', '**/obj/**', '--exclude-by-file', '**/Program.cs',
                '--does-not-return-attribute', 'DoesNotReturnAttribute', '--include', '[Manuals]*'
            )
            Check  = @{ Trx = 'Manuals.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx'; Floor = 1 }
        }
        @{
            Name        = 'Run integration tests with coverage'
            Needs       = @('Build with dotnet', 'Install dotnet-coverage')
            Inputs      = @('*', '!Manuals.Tests.Unit/*')
            Environment = @{ ASPNETCORE_ENVIRONMENT = 'Development'; RedisDatabase = '1' }
            Remove      = @('Manuals.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx')
            Run         = @(
                'pwsh', '-NoProfile', '-File', 'scripts/gate/Use-IntegrationRedis.ps1', 'dotnet-coverage', 'collect',
                'dotnet test --project Manuals.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Manuals.Tests.Integration/bin/Release/net11.0/TestResults',
                '-f', 'xml', '-o', 'coverage-integration.xml', '-s', 'coverage.settings.xml'
            )
            Check       = @{ Trx = 'Manuals.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx'; Floor = 1 }
        }
        @{ Name = 'dotnet publish'; Delivery = $true }
        @{
            Name   = 'End Sonar analysis'
            Sonar  = 'end'
            Needs  = @('Begin Sonar analysis', 'Build with dotnet')
            Inputs = @('*')
            Run    = @('dotnet-sonarscanner', 'end')
        }
        @{ Name = 'Fail on open Sonar issues'; Sonar = 'issues'; Needs = @('End Sonar analysis'); Inputs = @('*') }
        @{ Name = 'Run the app on .NET 11'; Delivery = $true }
    )
}
