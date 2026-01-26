using System.Globalization;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

public class GameController : MonoBehaviour
{
    public static GameController Instance { get; private set; }
    public bool MouseBlockedByUI { get; private set; }

    public int numTilesX = 130;
    public int numTilesY = 130;

    public uint citySeed = 0;  // 0 = random seed from time

    // BSP config (fills gaps between L-System arterials)
    public int minBlockSize = 12;
    public int maxBlockSize = 35;
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

    public int numHumans = 1000;
    public int humanStartingHealth = 100;
    public int humanDamage = 0;
    public int humanVisionDistance = 10;
    public int humanTurnDelay = 1;

    public int numZombies = 1;
    public int zombieStartingHealth = 70;
    public int zombieDamage = 20;
    public int zombieVisionDistance = 6;
    public int zombieHearingDistance = 10;
    public int zombieTurnDelay = 3;

    public int audibleDecayTime = 20;

    public float turnDelayTime = 0.025f;

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

    // Use this for initialization
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
        numHumans = int.Parse(num);
        numHumansSlider.value = numHumans;
    }

    public void SetNumHumansSlider(float num)
    {
        numHumans = (int)num;
        numHumansInputField.text = numHumans.ToString();
    }

    public void SetNumZombiesInputField(string num)
    {
        numZombies = int.Parse(num);
        numZombiesSlider.value = numZombies;
    }

    public void SetNumZombiesSlider(float num)
    {
        numZombies = (int)num;
        numZombiesInputField.text = numZombies.ToString();
    }

    public void SetNumTilesXInputField(string num)
    {
        numTilesX = int.Parse(num);
    }

    public void SetNumTilesYInputField(string num)
    {
        numTilesY = int.Parse(num);
    }

    public void SetCitySeedInputField(string num)
    {
        citySeed = uint.Parse(num);
    }

    public void OnRegeneratePressed()
    {
        CreateUpdateGameControllerComponentEntity();
        CreateSpawnWorldComponentEntity();
    }

    public void SetHumanTurnDelay(string num)
    {
        humanTurnDelay = int.Parse(num);

        CreateUpdateGameControllerComponentEntity();
    }

    public void SetZombieTurnDelay(string num)
    {
        zombieTurnDelay = int.Parse(num);

        CreateUpdateGameControllerComponentEntity();
    }

    public void SetTurnDelayTimeInputField(string num)
    {
        turnDelayTime = float.Parse(num) / 1000;
        turnDelayTimeSlider.value = float.Parse(num);

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
