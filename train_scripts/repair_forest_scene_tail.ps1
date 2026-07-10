$path = (Resolve-Path (Join-Path $PSScriptRoot '..\Assets\ForestScene.unity')).Path
$text = [IO.File]::ReadAllText($path)

# Remove truncated last line if broken
if ($text -match 'guid:\s*$') {
    $text = $text -replace '(?m)^\s*- target: \{fileID: 5379734164976326424, guid:\s*$', ''
    Write-Host 'Removed truncated line'
}

# Ensure we end at rotation.y for rock_2b (14) before appending rest
$marker = @"
    - target: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      propertyPath: m_LocalRotation.y
      value: -0
      objectReference: {fileID: 0}
"@

if (-not $text.Contains('--- !u!4 &2061452270 stripped')) {
    $tail = @"

    - target: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      propertyPath: m_LocalRotation.z
      value: -0
      objectReference: {fileID: 0}
    - target: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      propertyPath: m_LocalEulerAnglesHint.x
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      propertyPath: m_LocalEulerAnglesHint.y
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      propertyPath: m_LocalEulerAnglesHint.z
      value: 0
      objectReference: {fileID: 0}
    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents:
    - targetCorrespondingSourceObject: {fileID: 4747769363380178338, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
      insertIndex: -1
      addedObject: {fileID: 2061452272}
  m_SourcePrefab: {fileID: 100100000, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
--- !u!4 &2061452270 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: 5379734164976326424, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
  m_PrefabInstance: {fileID: 2061452269}
  m_PrefabAsset: {fileID: 0}
--- !u!1 &2061452271 stripped
GameObject:
  m_CorrespondingSourceObject: {fileID: 4747769363380178338, guid: dbddac634fa731942ac550e346b3a0b8, type: 3}
  m_PrefabInstance: {fileID: 2061452269}
  m_PrefabAsset: {fileID: 0}
--- !u!64 &2061452272
MeshCollider:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 2061452271}
  m_Material: {fileID: 0}
  m_IncludeLayers:
    serializedVersion: 2
    m_Bits: 0
  m_ExcludeLayers:
    serializedVersion: 2
    m_Bits: 0
  m_LayerOverridePriority: 0
  m_IsTrigger: 0
  m_ProvidesContacts: 0
  m_Enabled: 1
  serializedVersion: 5
  m_Convex: 0
  m_CookingOptions: 30
  m_Mesh: {fileID: 659395014849626798, guid: b45fd7ba090482548ab4e4ebfcc7ae6d, type: 3}
--- !u!1660057539 &2147483647
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 1812948161}
  - {fileID: 337850200}
  - {fileID: 1929081591}
  - {fileID: 1815548663}
  - {fileID: 278231174}
  - {fileID: 705507995}
"@
    if ($text.Contains($marker)) {
        $text = $text.Replace($marker, $marker + $tail)
        Write-Host 'Appended prefab tail + SceneRoots'
    } else {
        $text = $text.TrimEnd() + $tail
        Write-Host 'Appended tail at EOF (marker not found)'
    }
}

[IO.File]::WriteAllText($path, $text)
Write-Host "Repaired: $path"
