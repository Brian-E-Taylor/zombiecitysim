using System.Globalization;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

public class GameController : MonoBehaviour
{
    public static GameController Instance { get; private set; }
    public bool MouseBlockedByUI { get; private set; }

    [Header("City Configuration")]
    public int numTilesX = 900;
    public int numTilesY = 900;
    public uint citySeed = 0;  // 0 = random seed from time
    public float turnDelayTime = 0.0f;

    [Header("BSP Block Subdivision")]
    public int minBlockSize = 15;
    public int maxBlockSize = 40;
    public float splitVariance = 0.2f;

    [Header("L-System Arterial Roads")]
    public bool enableLSystemArterials = true;
    public int lSystemIterations = 2;
    public float lSystemBranchAngle = 45f;
    public float lSystemSegmentLength = 20f;
    public int lSystemRoadWidth = 5;
    public int lSystemNumSeeds = 4;

    [Header("Alley Generation")]
    public bool enableAlleys = true;
    public int alleyMinRegionSize = 25;
    public float alleyDeadEndProbability = 0.3f;
    public int alleyMaxLength = 12;

    [Header("Human Configuration")]
    public int numHumans = 20000;
    public int humanStartingHealth = 100;
    public int humanDamage = 0;
    public int humanVisionDistance = 10;
    public int humanTurnDelay = 3;

    [Header("Zombie Configuration")]
    public int numZombies = 10;
    public int zombieStartingHealth = 70;
    public int zombieDamage = 20;
    public int zombieVisionDistance = 8;
    public int zombieHearingDistance = 16;
    public int zombieTurnDelay = 5;

    public int audibleDecayTime = 20;

    public InputField numHumansInputField;
    public Slider numHumansSlider;
    public InputField numZombiesInputField;
    public Slider numZombiesSlider;

    public InputField numTilesXInputField;
    public InputField numTilesYInputField;
    public InputField citySeedInputField;

    public InputField humanTurnDelayInputField;
    public InputField zombieTurnDelayInputField;
    public InputField turnDelayTimeInputField;
    public Slider turnDelayTimeSlider;

    private int _uiElementsBlocked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        _uiElementsBlocked = 0;
    }

    void Start()
    {
        numHumansInputField.text = numHumans.ToString();
        numHumansSlider.value = numHumans;
        numZombiesInputField.text = numZombies.ToString();
        numZombiesSlider.value = numZombies;

        numTilesXInputField.text = numTilesX.ToString();
        numTilesYInputField.text = numTilesY.ToString();
        if (citySeedInputField != null)
            citySeedInputField.text = citySeed.ToString();

        humanTurnDelayInputField.text = humanTurnDelay.ToString();
        zombieTurnDelayInputField.text = zombieTurnDelay.ToString();
        turnDelayTimeInputField.text = (turnDelayTime * 1000).ToString(CultureInfo.InvariantCulture);
        turnDelayTimeSlider.value = turnDelayTime * 1000;

        CreateSpawnWorldComponentEntity();
    }

    private void CreateUpdateGameControllerComponentEntity()
    {
        World.DefaultGameObjectInjectionWorld.EntityManager.CreateSingleton<UpdateGameControllerComponent>();
    }

    private void CreateSpawnWorldComponentEntity()
    {
        World.DefaultGameObjectInjectionWorld.EntityManager.CreateSingleton<SpawnWorld>();
    }

    public void SetNumHumansInputField(string num)
    {
        if (!int.TryParse(num, out var parsed)) return;
        numHumans = parsed;
        numHumansSlider.value = numHumans;
    }

    public void SetNumHumansSlider(float num)
    {
        numHumans = (int)num;
        numHumansInputField.text = numHumans.ToString();
    }

    public void SetNumZombiesInputField(string num)
    {
        if (!int.TryParse(num, out var parsed)) return;
        numZombies = parsed;
        numZombiesSlider.value = numZombies;
    }

    public void SetNumZombiesSlider(float num)
    {
        numZombies = (int)num;
        numZombiesInputField.text = numZombies.ToString();
    }

    public void SetNumTilesXInputField(string num)
    {
        if (int.TryParse(num, out var parsed))
            numTilesX = parsed;
    }

    public void SetNumTilesYInputField(string num)
    {
        if (int.TryParse(num, out var parsed))
            numTilesY = parsed;
    }

    public void SetCitySeedInputField(string num)
    {
        if (uint.TryParse(num, out var parsed))
            citySeed = parsed;
    }

    public void OnRegeneratePressed()
    {
        CreateUpdateGameControllerComponentEntity();
        CreateSpawnWorldComponentEntity();
    }

    public void SetHumanTurnDelay(string num)
    {
        if (!int.TryParse(num, out var parsed)) return;
        humanTurnDelay = parsed;

        CreateUpdateGameControllerComponentEntity();
    }

    public void SetZombieTurnDelay(string num)
    {
        if (!int.TryParse(num, out var parsed)) return;
        zombieTurnDelay = parsed;

        CreateUpdateGameControllerComponentEntity();
    }

    public void SetTurnDelayTimeInputField(string num)
    {
        if (!float.TryParse(num, out var parsed)) return;
        turnDelayTime = parsed / 1000;
        turnDelayTimeSlider.value = parsed;

        CreateUpdateGameControllerComponentEntity();
    }

    public void SetTurnDelayTimeSlider(float num)
    {
        turnDelayTime = num / 1000;
        turnDelayTimeInputField.text = num.ToString(CultureInfo.InvariantCulture);

        CreateUpdateGameControllerComponentEntity();
    }

    public void EnterBlockingUI()
    {
        MouseBlockedByUI = true;
        _uiElementsBlocked++;
    }

    public void ExitBlockingUI()
    {
        _uiElementsBlocked--;
        if (_uiElementsBlocked == 0)
            MouseBlockedByUI = false;
    }
}
