using UnityEngine;

[CreateAssetMenu(fileName = "HeroAnimatorCatalog", menuName = "Forest/Hero Animator Catalog")]
public sealed class HeroAnimatorCatalog : ScriptableObject
{
    const string ResourcePath = "HeroAnimatorCatalog";

    static HeroAnimatorCatalog _cached;

    [SerializeField] RuntimeAnimatorController jack;
    [SerializeField] RuntimeAnimatorController lily;

    public static RuntimeAnimatorController ForAgent(Component agent)
    {
        if (agent == null)
            return null;

        var catalog = _cached != null ? _cached : Resources.Load<HeroAnimatorCatalog>(ResourcePath);
        if (catalog == null)
            return null;

        _cached = catalog;
        return agent is LilyScript ? catalog.lily : catalog.jack;
    }
}
