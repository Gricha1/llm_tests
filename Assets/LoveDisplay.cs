using UnityEngine;
using TMPro;

public class LoveDisplay : MonoBehaviour
{
    [Tooltip("Если false — стикер/счётчик любви не отображается.")]
    [SerializeField] private bool show = true;
    [SerializeField] private LilyScript lily;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    private TMP_Text text;

    void Awake()
    {
        text = GetComponent<TMP_Text>();
        if (text != null)
            text.richText = true;
    }

    void LateUpdate()
    {
        if (text == null)
            return;

        lily = TrainingEnvSpace.FindPresentationLily();
        if (lily == null || !TrainingEnvSpace.ShouldShowHudForRole(EnvTrainingAgentRole.Lily) || !show)
        {
            text.enabled = false;
            return;
        }

        text.enabled = true;

        if (spriteAsset != null && text.spriteAsset != spriteAsset)
            text.spriteAsset = spriteAsset;

        text.text = $"<sprite=0> {lily.Love}";
    }
}
