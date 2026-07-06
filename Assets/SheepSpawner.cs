using System.Collections.Generic;
using UnityEngine;

public class SheepSpawner : MonoBehaviour
{
    [Header("Sheep Prefab")]
    [SerializeField] private GameObject sheepPrefab;

    [Header("Spawn Settings")]
    [SerializeField] private int sheepCount = 5;
    [SerializeField] private float y = 0.6237636f;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float respawnInterval = 2f; // секунд между попытками доп. спавна
    [SerializeField] private float maxDistanceFromSpawn = 12f; // овца возвращается в зону, если ушла дальше

    // Область спавна (из твоих координат)
    private readonly float minX = -20.8f;
    private readonly float maxX = -8.18f;
    private readonly float minZ = -7.4f;
    private readonly float maxZ = -2.11f;

    private Vector3 SpawnCenterLocal => new Vector3((minX + maxX) * 0.5f, y, (minZ + maxZ) * 0.5f);

    private List<GameObject> sheeps = new List<GameObject>();
    private float nextRespawnTime;
    private Transform _envRoot;

    static bool IsAlive(GameObject go) => go != null;

    private void Awake()
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
    }

    private Vector3 ToWorld(Vector3 localPos)
    {
        return _envRoot != null ? _envRoot.TransformPoint(localPos) : localPos;
    }

    private void Start()
    {
        nextRespawnTime = Time.time + respawnInterval;
    }

    private void Update()
    {
        if (Time.time < nextRespawnTime) return;
        nextRespawnTime = Time.time + respawnInterval;

        RemoveDestroyedSheep();
        if (sheeps.Count < sheepCount)
            SpawnOneSheep();
    }

    private void RemoveDestroyedSheep()
    {
        sheeps.RemoveAll(s => !IsAlive(s));
    }

    private void SpawnOneSheep()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Vector3 pos = ToWorld(new Vector3(
                Random.Range(minX, maxX),
                y,
                Random.Range(minZ, maxZ)
            ));

            bool tooClose = false;
            foreach (var sheep in sheeps)
            {
                if (!IsAlive(sheep))
                    continue;

                if (Vector3.Distance(pos, sheep.transform.position) < minDistance)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                GameObject sheep = Instantiate(
                    sheepPrefab,
                    pos,
                    Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                    transform
                );
                SetSheepSpawnArea(sheep);
                sheeps.Add(sheep);
                return;
            }
        }
    }

    private void SetSheepSpawnArea(GameObject sheepObj)
    {
        var wander = sheepObj.GetComponent<SheepWander>();
        if (wander != null)
            wander.SetSpawnArea(ToWorld(SpawnCenterLocal), maxDistanceFromSpawn);
    }

    public void ResetSheep()
    {
        ClearSheep();
        SpawnSheep();
    }

    private void SpawnSheep()
    {
        int attempts = 0;

        for (int i = 0; i < sheepCount; i++)
        {
            bool placed = false;

            while (!placed && attempts < 100)
            {
                attempts++;

                Vector3 pos = ToWorld(new Vector3(
                    Random.Range(minX, maxX),
                    y,
                    Random.Range(minZ, maxZ)
                ));

                bool tooClose = false;
                foreach (var sheep in sheeps)
                {
                    if (!IsAlive(sheep))
                        continue;

                    if (Vector3.Distance(pos, sheep.transform.position) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    GameObject sheep = Instantiate(
                        sheepPrefab,
                        pos,
                        Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                        transform
                    );
                    SetSheepSpawnArea(sheep);
                    sheeps.Add(sheep);
                    placed = true;
                }
            }

            if (attempts >= 100)
            {
                Debug.LogWarning("Не удалось разместить всех овец без пересечений");
                break;
            }
        }
    }

    private void ClearSheep()
    {
        foreach (var sheep in sheeps)
        {
            if (IsAlive(sheep))
                Destroy(sheep);
        }
        sheeps.Clear();
    }
}