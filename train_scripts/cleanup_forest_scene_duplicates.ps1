# Removes duplicate SceneRoots/tail blocks injected by repair_forest_scene_tail.ps1
param(
    [string]$ScenePath = (Join-Path $PSScriptRoot '..\Assets\ForestScene.unity')
)

$ScenePath = (Resolve-Path $ScenePath).Path
$text = [IO.File]::ReadAllText($ScenePath)

$sceneRootsBlock = @"
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

$rockTailBlock = @"
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
"@

$rootsRemoved = 0
while ($text.Contains($sceneRootsBlock)) {
    $text = $text.Replace($sceneRootsBlock, '')
    $rootsRemoved++
}

$firstTail = $text.IndexOf($rockTailBlock)
if ($firstTail -ge 0) {
    $before = $text.Substring(0, $firstTail + $rockTailBlock.Length)
    $after = $text.Substring($firstTail + $rockTailBlock.Length)
    $after = $after.Replace($rockTailBlock, '')
    $text = $before + $after
    $tailsRemoved = ([regex]::Matches($after, [regex]::Escape('--- !u!4 &2061452270 stripped'))).Count
} else {
    $tailsRemoved = 0
}

$orphanFragment = @"
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
"@

$orphansRemoved = 0
while ($text.Contains($orphanFragment)) {
    $text = $text.Replace($orphanFragment, '')
    $orphansRemoved++
}

$footer = @"
--- !u!1660057539 &9223372036854775807
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

$text = $text -replace '(?ms)--- !u!1660057539 &9223372036854775807\s+SceneRoots:.*?- \{fileID: 705507995\}\s*', ''
$text = $text.TrimEnd() + "`n" + $footer + "`n"

[IO.File]::WriteAllText($ScenePath, $text)
Write-Host "Removed SceneRoots blocks: $rootsRemoved"
Write-Host "Removed duplicate rock tails: $($tailsRemoved - 1)"
Write-Host "Removed orphan fragments: $orphansRemoved"
Write-Host "Cleaned: $ScenePath"
