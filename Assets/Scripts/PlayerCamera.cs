using UnityEngine;
using UnityEngine.Serialization;

public class PlayerCamera : MonoBehaviour
{
    public enum CameraView
    {
        Nose,
        NearChase,
        FarChase,
    }

    private PlayerControls controls;

    // Heading that follows the direction of travel
    private float _yaw;
    private float _yawVelocity;

    // Heading of the camera
    private float _viewYaw;
    private float _viewYawVelocity;
    private bool _lookActive;

    // Nose view's look angle, relative to the player's nose
    private float _noseLook;
    private float _noseLookVelocity;

    [Tooltip("What object the camera follows.")]
    [SerializeField] Rigidbody target;
    [Tooltip("View used when play starts. The View button cycles Nose → Near Chase → Far Chase.")]
    [SerializeField] CameraView currentView = CameraView.NearChase;

    [Header("Nose")]
    [Tooltip("Distance forward of the player's pivot when looking ahead (m).")]
    [SerializeField] float noseForward = 1f;
    [Tooltip("Distance behind the player's pivot when looking back (m).")]
    [SerializeField] float noseRear = 1.9f;
    [Tooltip("Distance out to the side of the player's pivot when looking left or right (m).")]
    [SerializeField] float noseSide = 0.9f;
    [Tooltip("Height above the player's pivot (m).")]
    [SerializeField] float noseHeight = 0.6f;

    [Header("Near Chase")]
    [Tooltip("Horizontal distance behind the player (m).")]
    [FormerlySerializedAs("distance")]
    [SerializeField] float nearDistance = 10f;
    [Tooltip("Height above the player's pivot (m).")]
    [FormerlySerializedAs("height")]
    [SerializeField] float nearHeight = 3.7f;

    [Header("Far Chase")]
    [Tooltip("Horizontal distance behind the player (m).")]
    [SerializeField] float farDistance = 16f;
    [Tooltip("Height above the player's pivot (m).")]
    [SerializeField] float farHeight = 5.5f;

    [Header("Chase")]
    [Tooltip("Height above the player's pivot that the chase views aim at.")]
    [SerializeField] float focusHeight = 1f;

    [Header("Heading")]
    [Tooltip("Approximate time for the camera to catch up to a change in heading.")]
    [SerializeField] float rotationSmoothTime = 0.3f;
    [Tooltip("Below this speed (m/s) the camera keeps its last heading.")]
    [SerializeField] float minHeadingSpeed = 1f;
    [Tooltip("At or above this speed (m/s) the camera fully follows the direction of travel.")]
    [SerializeField] float fullHeadingSpeed = 4f;

    [Header("Look")]
    [Tooltip("How far the stick must be pushed before the camera looks round.")]
    [Range(0f, 1f)]
    [SerializeField] float lookDeadZone = 0.4f;
    [Tooltip("Approximate time for the camera to swing to the look direction and back.")]
    [SerializeField] float lookSmoothTime = 0.15f;

    void Awake()
    {
        controls = new();
    }

    void OnEnable()
    {
        controls.Camera.Look.Enable();
        controls.Camera.View.Enable();
    }

    void OnDisable()
    {
        controls.Camera.Look.Disable();
        controls.Camera.View.Disable();
    }

    void OnDestroy()
    {
        controls.Dispose();
    }

    void Start()
    {
        if (target == null)
        {
            Debug.LogWarning($"{nameof(PlayerCamera)} has no target assigned.", this);
            enabled = false;
            return;
        }

        // Sets the initial heading to match the player's direction
        _yaw = HeadingMathsHelpers.FromDirection(target.transform.forward, 0f);
        _viewYaw = _yaw;
        Follow();
    }

    void LateUpdate()
    {
        if (controls.Camera.View.WasPressedThisFrame())
        {
            CycleView();
        }

        // Keep tracking the travel heading in every view, so the chase views are ready when switched to
        UpdateHeading();

        if (currentView == CameraView.Nose)
        {
            ResetLook();
            UpdateNoseLook();
        }
        else
        {
            UpdateLook();
            ResetNoseLook();
        }

        Follow();
    }

    private void CycleView()
    {
        currentView = (CameraView)(((int)currentView + 1) % System.Enum.GetValues(typeof(CameraView)).Length);
    }

    private void UpdateHeading()
    {
        Vector3 travel = Vector3.ProjectOnPlane(target.linearVelocity, Vector3.up);
        float weight = Mathf.InverseLerp(minHeadingSpeed, fullHeadingSpeed, travel.magnitude);
        float delta = weight > 0f ? HeadingMathsHelpers.AngleToTurn(_yaw, HeadingMathsHelpers.FromDirection(travel, _yaw), _yawVelocity) : 0f;
        float smoothTime = rotationSmoothTime / Mathf.Max(weight, 0.05f);

        _yaw = Mathf.SmoothDamp(_yaw, _yaw + delta, ref _yawVelocity, smoothTime);
        _yaw = Mathf.Repeat(_yaw, 360f);
    }

    private void UpdateLook()
    {
        Vector2 stickVector = controls.Camera.Look.ReadValue<Vector2>();

        bool held = stickVector.magnitude >= lookDeadZone;

        if (held && !_lookActive)
        {
            _lookActive = true;
            _viewYawVelocity = _yawVelocity;
        }

        if (!_lookActive)
        {
            _viewYaw = _yaw;
            return;
        }

        // While Look action held, look relative to the player's nose so strafing (drifting) or reversing doesn't move the view.
        float targetView = held
            ? HeadingMathsHelpers.FromDirection(target.transform.forward, _viewYaw) + HeadingMathsHelpers.From2DInput(stickVector)
            : _yaw;

        float delta = HeadingMathsHelpers.AngleToTurn(_viewYaw, targetView, _viewYawVelocity);
        _viewYaw = Mathf.SmoothDamp(_viewYaw, _viewYaw + delta, ref _viewYawVelocity, lookSmoothTime);
        _viewYaw = Mathf.Repeat(_viewYaw, 360f);

        if (!held && Mathf.Abs(Mathf.DeltaAngle(_viewYaw, _yaw)) < 0.5f)
        {
            _lookActive = false;
            _viewYaw = _yaw;
        }
    }

    // Drop any chase look-around in progress while in the nose view
    private void ResetLook()
    {
        _lookActive = false;
        _viewYaw = _yaw;
        _viewYawVelocity = 0f;
    }

    // Look angle relative to the nose and stick
    private void UpdateNoseLook()
    {
        Vector2 stickVector = controls.Camera.Look.ReadValue<Vector2>();
        float targetLook = stickVector.magnitude >= lookDeadZone
            ? HeadingMathsHelpers.From2DInput(stickVector)
            : 0f;

        float delta = HeadingMathsHelpers.AngleToTurn(_noseLook, targetLook, _noseLookVelocity);
        _noseLook = Mathf.SmoothDamp(_noseLook, _noseLook + delta, ref _noseLookVelocity, lookSmoothTime);
        _noseLook = Mathf.DeltaAngle(0f, _noseLook);
    }

    // Start the nose view facing forward each time it's switched to
    private void ResetNoseLook()
    {
        _noseLook = 0f;
        _noseLookVelocity = 0f;
    }

    // Move the camera to its new position and rotation
    private void Follow()
    {
        switch (currentView)
        {
            case CameraView.Nose:
                FollowNose();
                break;
            case CameraView.NearChase:
                FollowChase(nearDistance, nearHeight);
                break;
            case CameraView.FarChase:
                FollowChase(farDistance, farHeight);
                break;
        }
    }

    private void FollowNose()
    {
        // Move camera round the player to the edge facing the look direction
        float radians = _noseLook * Mathf.Deg2Rad;
        float along = Mathf.Cos(radians);
        float across = Mathf.Sin(radians);
        Vector3 offset = new Vector3(
            across * noseSide,
            noseHeight,
            along * (along >= 0f ? noseForward : noseRear));

        // Fixed to the player in metres (not via TransformPoint, so that scale doesn't stretch the offset)
        Quaternion playerRotation = target.transform.rotation;
        transform.position = target.transform.position + playerRotation * offset;
        transform.rotation = playerRotation * Quaternion.Euler(0f, _noseLook, 0f);
    }

    private void FollowChase(float distance, float height)
    {
        Vector3 focus = target.transform.position + Vector3.up * focusHeight;
        Quaternion heading = Quaternion.Euler(0f, _viewYaw, 0f);

        transform.position = target.transform.position
            - heading * Vector3.forward * distance
            + Vector3.up * height;

        // World up keeps the horizon level when the player pitches, rolls or flips
        transform.rotation = Quaternion.LookRotation(focus - transform.position, Vector3.up);
    }
}
