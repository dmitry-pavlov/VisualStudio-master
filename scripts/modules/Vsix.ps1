Set-StrictMode -Version Latest

New-Module -ScriptBlock {
    $gitHubDirectory = Join-Path $rootDirectory src\GitHub.VisualStudio

    function Get-VsixManifestPath {
        Join-Path $gitHubDirectory source.extension.vsixmanifest
    }

    function Get-VsixManifestXml {
        $xmlLines = Get-Content (Get-VsixManifestPath)
        # If we don't explicitly join the lines with CRLF, comments in the XML will
        # end up with LF line-endings, which will make Git spew a warning when we
        # try to commit the version bump.
        $xmlText = $xmlLines -join [System.Environment]::NewLine

        [xml] $xmlText
    }

    function Read-CurrentVersionVsix {
        [System.Version] (Get-VsixManifestXml).PackageManifest.Metadata.Identity.Version
    }

    function Write-VersionVsixManifest([System.Version]$version) {

        $document = Get-VsixManifestXml

        $numberOfReplacements = 0
        $document.PackageManifest.Metadata.Identity.Version = $version.ToString()

        $document.Save((Get-VsixManifestPath))
    }

    Export-ModuleMember -Function Read-CurrentVersionVsix,Write-VersionVsixManifest
}