function UpdateSourceWithBuildInfo ( $projectDir, $buildNumber, $version ) {
    $commit = Get-Git-Commit-Short
    UpdateCommonAssemblyInfo $projectDir $buildNumber $version $commit

    $versionInfoFile = [io.path]::combine($projectDir, "src", "Raven.Client", "Properties", "VersionInfo.cs")
    UpdateRavenVersion $projectDir $buildNumber $version $commit $versionInfoFile

    UpdateCsprojWithVersionInfo $projectDir $version
}

function UpdateCsprojWithVersionInfo ( $projectDir, $version ) {
    # This is a workaround for the following issue:
    # dotnet pack - version suffix missing from ProjectReference: https://github.com/NuGet/Home/issues/4337

    Write-Host "Set version in Directory.build.props..."

    $src = $(Join-Path $projectDir -ChildPath "src");
    $testDriverCsproj = [io.path]::Combine($src, "Raven.TestDriver", "Raven.TestDriver.csproj")
    $clientCsproj = [io.path]::Combine($src, "Raven.Client", "Raven.Client.csproj")

    # https://github.com/Microsoft/msbuild/issues/1721
    UpdateVersionInFile $testDriverCsproj $version
    UpdateVersionInFile $clientCsproj $version

    $buildProps = Join-Path -Path $projectDir -ChildPath "Directory.Build.props"
    UpdateVersionInFile $buildProps $version
}

function UpdateVersionInFile ( $file, $version ) {
    $versionPattern = [regex]'(?sm)<Version>[A-Za-z0-9-\.\r\n\s]*</Version>'
    $inputText = [System.IO.File]::ReadAllText($file)
    $result = $versionPattern.Replace($inputText, "<Version>$version</Version>")
    [System.IO.File]::WriteAllText($file, $result, [System.Text.Encoding]::UTF8)
}

function UpdateCommonAssemblyInfo ( $projectDir, $buildNumber, $version, $commit ) {
    $assemblyInfoFile = [io.path]::combine($projectDir, "src", "CommonAssemblyInfo.cs")
    Write-Host "Set version in $assemblyInfoFile..."

    $fileVersion = "$($version.Split("-")[0]).$buildNumber"

    $content = (Get-Content $assemblyInfoFile) |
    Foreach-Object { $_ -replace "{commit}", $commit } |
    Foreach-Object { $_ -replace '\[assembly: AssemblyFileVersion\(".*"\)\]', "[assembly: AssemblyFileVersion(""$fileVersion"")]" } |
    Foreach-Object { $_ -replace '\[assembly: AssemblyInformationalVersion\(".*"\)\]', "[assembly: AssemblyInformationalVersion(""$version"")]" }

    Set-Content -Path $assemblyInfoFile -Value $content -Encoding UTF8
}

function UpdateRavenVersion ( $projectDir, $buildNumber, $version, $commit, $versionInfoFile ) {
    write-host "Set version in $versionInfoFile"

    $content = (Get-Content $versionInfoFile) |
        Foreach-Object { $_ -replace 'RavenVersion\(Build = ".*", CommitHash = ".*", Version = "4.0", FullVersion = ".*"\)', "RavenVersion(Build = ""$buildNumber"", CommitHash = ""$commit"", Version = ""4.0"", FullVersion = ""$version"")" }

    Set-Content -Path $versionInfoFile -Value $content -Encoding UTF8
}

function Get-Git-Commit-Short
{
    $(Get-Git-Commit).Substring(0, 7)
}

function Get-Git-Commit
{
    if (Get-Command "git" -ErrorAction SilentlyContinue) {
        $sha1 = & git rev-parse HEAD
        CheckLastExitCode
        $sha1
    }
    else {
        return "0000000000000000000000000000000000000000"
    }
}