# Fixes broken YAML references in ForestScene.unity
param(
    [string]$ScenePath = (Join-Path $PSScriptRoot '..\Assets\ForestScene.unity')
)

$ScenePath = (Resolve-Path $ScenePath).Path
$content = [IO.File]::ReadAllText($ScenePath)

function Get-DefinedIds([string]$text) {
    $set = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($m in [regex]::Matches($text, '(?m)^--- !u!\d+ &(\d+)')) {
        [void]$set.Add([int]$m.Groups[1].Value)
    }
    return $set
}

function Get-PrefabInstanceIds([string]$text) {
    $set = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($m in [regex]::Matches($text, '(?m)^--- !u!1001 &(\d+)')) {
        [void]$set.Add([int]$m.Groups[1].Value)
    }
    return $set
}

$defined = Get-DefinedIds $content
$prefabInstances = Get-PrefabInstanceIds $content

# 1) Recreate missing Env child container 2075869641 if referenced but absent
if (-not $defined.Contains(2075869641)) {
    $refs207 = [regex]::Matches($content, '2075869641').Count
    if ($refs207 -gt 0) {
        $block = @"
--- !u!1 &2075869640
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 2075869641}
  m_Layer: 0
  m_Name: RocksContainer
  m_TagString: Untagged
  m_Icon: {fileID: 0}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &2075869641
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 2075869640}
  serializedVersion: 2
  m_LocalRotation: {x: 0.45886743, y: 0, z: 0, w: 0.8885048}
  m_LocalPosition: {x: 6.4294615, y: 10.721214, z: 7.0504208}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_ConstrainProportionsScale: 0
  m_Children: []
  m_Father: {fileID: 1812948161}
  m_LocalEulerAnglesHint: {x: 54.628, y: 0, z: 0}

"@
        $insertAt = $content.IndexOf("--- !u!4 &1812948161")
        if ($insertAt -lt 0) { throw "Env transform 1812948161 not found" }
        $content = $content.Insert($insertAt, $block)
        $defined = Get-DefinedIds $content
        Write-Host "Recreated missing Transform 2075869641 (RocksContainer)"
    }
}

# 2) Remove stripped transforms whose PrefabInstance block is missing (+ related stripped GO / added colliders)
$removedTransforms = [System.Collections.Generic.HashSet[int]]::new()
$removedGameObjects = [System.Collections.Generic.HashSet[int]]::new()
$removedComponents = [System.Collections.Generic.HashSet[int]]::new()

while ($true) {
    $changed = $false
    foreach ($m in [regex]::Matches($content, '(?ms)^--- !u!4 &(\d+) stripped\r?\nTransform:\r?\n.*?\r?\n  m_PrefabInstance: \{fileID: (\d+)\}')) {
        $transformId = [int]$m.Groups[1].Value
        $prefabId = [int]$m.Groups[2].Value
        if ($prefabInstances.Contains($prefabId)) { continue }

        $start = $m.Index
        $end = $content.IndexOf("`n--- !u!", $start + 1)
        if ($end -lt 0) { $end = $content.Length }
        $content = $content.Remove($start, $end - $start)
        [void]$removedTransforms.Add($transformId)
        $changed = $true
        Write-Host "Removed dangling stripped Transform $transformId (missing PrefabInstance $prefabId)"
        break
    }
    if (-not $changed) { break }
}

# stripped GameObjects pointing to missing prefab instances
while ($true) {
    $changed = $false
    foreach ($m in [regex]::Matches($content, '(?ms)^--- !u!1 &(\d+) stripped\r?\nGameObject:\r?\n.*?\r?\n  m_PrefabInstance: \{fileID: (\d+)\}')) {
        $goId = [int]$m.Groups[1].Value
        $prefabId = [int]$m.Groups[2].Value
        if ($prefabInstances.Contains($prefabId)) { continue }

        $start = $m.Index
        $end = $content.IndexOf("`n--- !u!", $start + 1)
        if ($end -lt 0) { $end = $content.Length }
        $content = $content.Remove($start, $end - $start)
        [void]$removedGameObjects.Add($goId)
        $changed = $true
        Write-Host "Removed dangling stripped GameObject $goId (missing PrefabInstance $prefabId)"
        break
    }
    if (-not $changed) { break }
}

# orphan MeshColliders referencing removed GameObjects
while ($true) {
    $changed = $false
    foreach ($m in [regex]::Matches($content, '(?ms)^--- !u!64 &(\d+)\r?\nMeshCollider:\r?\n(?:.*?\r?\n)*?  m_GameObject: \{fileID: (\d+)\}')) {
        $compId = [int]$m.Groups[1].Value
        $goId = [int]$m.Groups[2].Value
        if (-not $removedGameObjects.Contains($goId)) { continue }

        $start = $m.Index
        $end = $content.IndexOf("`n--- !u!", $start + 1)
        if ($end -lt 0) { $end = $content.Length }
        $content = $content.Remove($start, $end - $start)
        [void]$removedComponents.Add($compId)
        $changed = $true
        Write-Host "Removed orphan MeshCollider $compId for GameObject $goId"
        break
    }
    if (-not $changed) { break }
}

# 3) Remove child-list entries pointing to removed / undefined transforms
$defined = Get-DefinedIds $content
$toRemoveFromChildren = [System.Collections.Generic.HashSet[int]]::new()
foreach ($id in $removedTransforms) { [void]$toRemoveFromChildren.Add($id) }

foreach ($m in [regex]::Matches($content, '(?m)^  - \{fileID: (\d+)\}')) {
    $id = [int]$m.Groups[1].Value
    if (-not $defined.Contains($id)) { [void]$toRemoveFromChildren.Add($id) }
}

foreach ($id in $toRemoveFromChildren) {
    $pattern = "(?m)^  - \{fileID: $id\}\r?\n"
    $newContent = [regex]::Replace($content, $pattern, '')
    if ($newContent -ne $content) {
        Write-Host "Removed child ref {fileID: $id}"
        $content = $newContent
    }
}

# 4) Reparent prefab instances that still point to undefined parents -> Env transform
$defined = Get-DefinedIds $content
$envTransform = 1812948161
if ($defined.Contains($envTransform)) {
    foreach ($m in [regex]::Matches($content, '(?m)^    m_TransformParent: \{fileID: (\d+)\}')) {
        $parentId = [int]$m.Groups[1].Value
        if ($defined.Contains($parentId)) { continue }
        if ($parentId -eq 2075869641 -and $defined.Contains(2075869641)) { continue }
        $old = "    m_TransformParent: {fileID: $parentId}"
        $new = "    m_TransformParent: {fileID: $envTransform}"
        $content = $content.Replace($old, $new)
        Write-Host "Reparented instances from missing parent $parentId -> Env"
    }
}

# 5) Restore main Env Jack stripped objects if prefab instance exists but transform/agent blocks are missing
$jackPrefabId = 1293445288
$jackTransformId = 2146228767
$jackAgentId = 2146228746
$jackGoId = 441588237
$envTransformId = 1812948161

if ($content -match "--- !u!1001 &$jackPrefabId" -and -not ($defined.Contains($jackTransformId))) {
    $jackBlock = @"

--- !u!4 &$jackTransformId stripped
Transform:
  m_CorrespondingSourceObject: {fileID: 3965619694517639979, guid: 2f0b04bbc5c346843a2b7b4b9765ea30, type: 3}
  m_PrefabInstance: {fileID: $jackPrefabId}
  m_PrefabAsset: {fileID: 0}
--- !u!114 &$jackAgentId stripped
MonoBehaviour:
  m_CorrespondingSourceObject: {fileID: 644675218892376521, guid: 2f0b04bbc5c346843a2b7b4b9765ea30, type: 3}
  m_PrefabInstance: {fileID: $jackPrefabId}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: $jackGoId}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: fd59d9ea2e0be364db1a23a37a3d8423, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 

"@
    $marker = "  m_SourcePrefab: {fileID: 100100000, guid: 2f0b04bbc5c346843a2b7b4b9765ea30, type: 3}`r`n--- !u!1001 &1294077133"
    if ($content.Contains($marker)) {
        $content = $content.Replace($marker, "  m_SourcePrefab: {fileID: 100100000, guid: 2f0b04bbc5c346843a2b7b4b9765ea30, type: 3}$jackBlock--- !u!1001 &1294077133")
        Write-Host "Restored Jack Transform $jackTransformId and Agent $jackAgentId"
    }
}

$defined = Get-DefinedIds $content
$envChildNeedle = "  - {fileID: 2075869641}`r`n  - {fileID: 1188478222}"
$envChildReplacement = "  - {fileID: 2075869641}`r`n  - {fileID: $jackTransformId}`r`n  - {fileID: 1188478222}"
if ($defined.Contains($jackTransformId) -and $content.Contains($envChildNeedle) -and $content -notmatch "  - \{fileID: $jackTransformId\}") {
    $content = $content.Replace($envChildNeedle, $envChildReplacement)
    Write-Host "Re-added Jack to Env children"
}

[IO.File]::WriteAllText($ScenePath, $content)

# Report remaining broken refs
$defined = Get-DefinedIds ([IO.File]::ReadAllText($ScenePath))
$refs = [regex]::Matches([IO.File]::ReadAllText($ScenePath), '\{fileID: (\d+)\}') | ForEach-Object { [int]$_.Groups[1].Value }
$missing = $refs | Where-Object { $_ -ne 0 -and -not $defined.Contains($_) } | Sort-Object -Unique
Write-Host "Remaining missing referenced IDs: $($missing.Count)"
if ($missing.Count -gt 0 -and $missing.Count -le 30) { $missing }

