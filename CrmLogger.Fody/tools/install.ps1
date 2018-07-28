param($installPath, $toolsPath, $package, $project)

#save the project file first - this commits the changes made by nuget before this     script runs.
$project.Save()

function RemoveForceProjectLevelHack($project)
{
    Write-Host "RemoveForceProjectLevelHack" 
	Foreach ($item in $project.ProjectItems) 
	{
		if ($item.Name -eq "Fody_ToBeDeleted.txt")
		{
			$item.Delete()
		}
	}
}

function FlushVariables()
{
    Write-Host "Flushing environment variables"
    $env:FodyLastProjectPath = ""
    $env:FodyLastWeaverName = ""
    $env:FodyLastXmlContents = ""
}

function Update-FodyConfig($addinName, $project)
{
	Write-Host "Update-FodyConfig" 
    $fodyWeaversPath = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($project.FullName), "FodyWeavers.xml")

	$FodyLastProjectPath = $env:FodyLastProjectPath
	$FodyLastWeaverName = $env:FodyLastWeaverName
	$FodyLastXmlContents = $env:FodyLastXmlContents
	
	if (
		($FodyLastProjectPath -eq $project.FullName) -and 
		($FodyLastWeaverName -eq $addinName))
	{
        Write-Host "Upgrade detected. Restoring content for $addinName"
		[System.IO.File]::WriteAllText($fodyWeaversPath, $FodyLastXmlContents)
        FlushVariables
		return
	}
	
    FlushVariables

    $xml = [xml](get-content $fodyWeaversPath)

    $weavers = $xml["Weavers"]
    $node = $weavers.SelectSingleNode($addinName)

    if (-not $node)
    {
        Write-Host "Appending node"
        $newNode = $xml.CreateElement($addinName)
        $weavers.AppendChild($newNode)
    }

    $xml.Save($fodyWeaversPath)
}

function Fix-ReferencesCopyLocal($package, $project)
{
    Write-Host "Fix-ReferencesCopyLocal $($package.Id)"
    $asms = $package.AssemblyReferences | %{$_.Name}
 
    foreach ($reference in $project.Object.References)
    {
        if ($asms -contains $reference.Name + ".dll")
        {
            if($reference.CopyLocal -eq $true)
            {
                $reference.CopyLocal = $false;
            }
        }
    }
}

function UnlockWeaversXml($project)
{
    $fodyWeaversProjectItem = $project.ProjectItems.Item("FodyWeavers.xml");
    if ($fodyWeaversProjectItem)
    {
        $fodyWeaversProjectItem.Open("{7651A701-06E5-11D1-8EBD-00A0C90F26EA}")
        $fodyWeaversProjectItem.Save()
		$fodyWeaversProjectItem.Document.Close()
    }   
}

UnlockWeaversXml($project)

RemoveForceProjectLevelHack $project

Update-FodyConfig $package.Id.Replace(".Fody", "") $project

Fix-ReferencesCopyLocal $package $project

$project.ExecuteCommand("Project.UnloadProject")

#Load the csproj file into an xml object
$xml = [XML] (gc $project.FullName)

#grab the namespace from the project element so your xpath works.
$nsmgr = New-Object System.Xml.XmlNamespaceManager -ArgumentList $xml.NameTable
$nsmgr.AddNamespace('a',$xml.Project.GetAttribute("xmlns"))

#link the service designer to the service.cs
$node = $xml.Project.SelectSingleNode("//a:Import[contains(@Project, 'Fody.targets')]", $nsmgr)
$node.Condition = $node.Condition + " And '`$(Configuration)' == 'Release'"

#save the changes.
$xml.Save($project.FullName)

$project.Save()
