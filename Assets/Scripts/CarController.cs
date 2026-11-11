using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class CarController : DebuggableObject
{
    private Rigidbody _rb;
    private PlayerControls _controls;
    private float _accelRate = 0;
    private float _steerRate = 0;
    private bool _inReverse = false;
    private bool _handbrake = false;

    [Header("Suspension")]
    [Tooltip("One point per wheel. Each casts a ray straight down along its own up axis to find the ground, and is where the spring, drive and grip forces are applied.")]
    [SerializeField] Transform[] suspensionFixedPoint;
    [Tooltip("Layers the wheel rays treat as ground.")]
    [SerializeField] LayerMask groundMask = ~0;
    [Tooltip("Length of the spring and wheel ray (m). If the ground is further away than this, the wheel is in the air and applies no force.")]
    [SerializeField] float restLength = 0.5f;
    [Tooltip("Spring force per metre of compression (N/m). Higher holds the car up more firmly and makes the ride stiffer.")]
    [SerializeField] float stiffness = 24500f;
    [Tooltip("Resists how fast the spring compresses or extends (N per m/s). Higher stops the car bouncing; too high feels harsh over bumps.")]
    [SerializeField] float damping = 2500f;

    [Header("Steering")]
    [Tooltip("Wheel points that turn with steering input. Usually front wheels.")]
    [SerializeField] Transform[] steeringFixedPoint;
    [Tooltip("Steering angle at full lock when stationary (degrees). At top speed it drops to 40% of this.")]
    [SerializeField] float maxSteerAngle = 55f;

    [Header("Acceleration")]
    [Tooltip("Drive acceleration at full throttle from a standstill (m/s²). It tapers to zero as the car nears its top speed.")]
    [SerializeField] float acceleration = 15f;
    [Tooltip("Deceleration at full brake (m/s²).")]
    [SerializeField] float braking = 30f;
    [Tooltip("Top speed going forwards (m/s).")]
    [SerializeField] float maxSpeed = 120f;
    [Tooltip("Top speed in reverse (m/s).")]
    [SerializeField] float maxReverseSpeed = 40f;
    [Tooltip("Constant slowing force from the tyres (m/s²), always applied while rolling.")]
    [SerializeField] float rollingResistance = 0.5f;
    [Tooltip("Extra slowing when off the throttle (m/s²). Full at zero throttle and fades out as throttle is applied.")]
    [SerializeField] float engineBraking = 3f;

    [Header("Grip")]
    [Tooltip("How strongly the front tyres cancel sideways sliding. Higher feels more planted; lower lets the front slide more easily.")]
    [SerializeField] float frontSideGrip = 10f;
    [Tooltip("How strongly the rear tyres cancel sideways sliding. Higher feels more planted; lower lets the rear step out more easily.")]
    [SerializeField] float rearSideGrip = 11f;
    [Tooltip("Most sideways acceleration the front tyres can give (m/s²). When cornering needs more than this, the front washes wide (understeer).")]
    [SerializeField] float maxFrontSideAccel = 12f;
    [Tooltip("Most sideways acceleration the rear tyres can give (m/s²). When cornering needs more than this, the rear slides out (oversteer/drift).")]
    [SerializeField] float maxRearSideAccel = 15f;
    [Tooltip("Fraction of the rear grip limit lost at full throttle. Higher makes it easier to kick the rear out with the throttle.")]
    [Range(0f, 1f)]
    [SerializeField] float throttleGripLoss = 0.2f;

    [Header("Handbrake")]
    [Tooltip("Fraction of the rear tyres' sideways grip kept while the handbrake locks them. Lower makes the rear slide out more.")]
    [Range(0f, 1f)]
    [SerializeField] float handbrakeGrip = 0.3f;
    [Tooltip("Slowing from each locked rear tyre skidding along the ground (m/s²). Higher scrubs off more speed while the handbrake is held.")]
    [SerializeField] float skidDrag = 8f;

    void Awake()
    {
        _controls = new();
        _rb = GetComponent<Rigidbody>();

        _controls.Car.Accel.Enable();
        _controls.Car.Decel.Enable();
        _controls.Car.Steer.Enable();
        _controls.Car.Handbrake.Enable();
    }

    void OnDisable()
    {
        _controls.Car.Accel.Disable();
        _controls.Car.Decel.Disable();
        _controls.Car.Steer.Disable();
        _controls.Car.Handbrake.Disable();
    }

    void OnDestroy()
    {
        _controls.Dispose();
    }

    private void Update()
    {
        _steerRate = SetSteerRate();
        _accelRate = SetAccelRate();
        _handbrake = _controls.Car.Handbrake.IsPressed();
    }

    private float SetSteerRate()
    {
        var stickAngle = _controls.Car.Steer.ReadValue<Vector2>();
        return stickAngle.x;
    }

    private float SetAccelRate()
    {
        var accel = _controls.Car.Accel.ReadValue<float>();
        var decel = _controls.Car.Decel.ReadValue<float>();
        return accel - decel;
    }

    void FixedUpdate()
    {
        UpdateGear(_accelRate);

        foreach (Transform point in steeringFixedPoint)
        {
            ApplySteering(point, _steerRate);
        }

        foreach (Transform point in suspensionFixedPoint)
        {
            // TODO: add some air resistance to slow down the car when in the air
            // TODO: add some suspension travel to allow the wheel to move up and down when in the air
            // TODO: remove grip when tyre is in the air
            // wheel is in the air, so no forces added
            if (!Physics.Raycast(point.position, -point.up, out RaycastHit hit, restLength, groundMask))
            {
                continue;
            }

            bool locked = _handbrake && !IsFrontWheel(point);

            ApplySpring(point, hit);
            if (locked)
            {
                ApplySkid(point, hit);
            }
            else
            {
                ApplyAccel(point, hit, _accelRate);
            }
            ApplyGrip(point, hit, _accelRate, locked);
        }
    }

    private bool IsFrontWheel(Transform point)
    {
        return System.Array.IndexOf(steeringFixedPoint, point) >= 0;
    }

    private void UpdateGear(float value)
    {
        // Reverse needed to allow braking to a stop rather than braking past a stop
        if (value > 0f)
        {
            _inReverse = false;
        }
        else if (value < 0f && _rb.linearVelocity.magnitude < 0.5f)
        {
            _inReverse = true;
        }
    }

    private void ApplyAccel(Transform point, RaycastHit hit, float value)
    {
        Debug.Log($"Accel: {value}");

        Vector3 forward = Vector3.ProjectOnPlane(point.forward, hit.normal).normalized;
        Vector3 velocity = _rb.GetPointVelocity(point.position);
        float speed = Vector3.Dot(forward, velocity);

        float targetAccel;
        if (value < 0f && !_inReverse)
        {
            // Brakes against the direction of travel, without overshooting past a standstill
            float brake = Mathf.Min(-value * braking, Mathf.Abs(speed) / Time.fixedDeltaTime);
            targetAccel = -Mathf.Sign(speed) * brake;
        }
        else
        {
            // Accelerates in the direction of travel, but slows down as it approaches the max speed
            float limit = value >= 0f ? maxSpeed : maxReverseSpeed;
            float power = 1f - Mathf.Clamp01(Mathf.Abs(speed) / limit);
            targetAccel = value * acceleration * power;
        }

        float massPerWheel = _rb.mass / suspensionFixedPoint.Length;
        _rb.AddForceAtPosition(forward * targetAccel * massPerWheel, point.position);

        // Slows the car when coasting
        float coast = rollingResistance + engineBraking * (1f - Mathf.Abs(value));
        float coastAccel = -Mathf.Sign(speed) * Mathf.Min(coast, Mathf.Abs(speed) / Time.fixedDeltaTime);
        _rb.AddForceAtPosition(forward * coastAccel * massPerWheel, point.position);

        DrawDebugRay(hit.point, forward * targetAccel, Color.green);
    }

    private void ApplySkid(Transform point, RaycastHit hit)
    {
        Vector3 forward = Vector3.ProjectOnPlane(point.forward, hit.normal).normalized;
        float speed = Vector3.Dot(forward, _rb.GetPointVelocity(point.position));
        float skidAccel = -Mathf.Sign(speed) * Mathf.Min(skidDrag, Mathf.Abs(speed) / Time.fixedDeltaTime);
        float massPerWheel = _rb.mass / suspensionFixedPoint.Length;
        _rb.AddForceAtPosition(forward * skidAccel * massPerWheel, point.position);

        DrawDebugRay(hit.point, forward * skidAccel, Color.magenta);
    }

    private void ApplyGrip(Transform point, RaycastHit hit, float value, bool locked)
    {
        Vector3 velocity = _rb.GetPointVelocity(point.position);
        float massPerWheel = _rb.mass / suspensionFixedPoint.Length;

        bool isFront = IsFrontWheel(point);
        float grip = isFront ? frontSideGrip : rearSideGrip;
        float maxSideAccel = isFront ? maxFrontSideAccel : maxRearSideAccel;

        if (locked)
        {
            // A locked, skidding tyre has far less sideways grip
            grip *= handbrakeGrip;
            maxSideAccel *= handbrakeGrip;
        }
        else if (!isFront)
        {
            // Locked wheels aren't driven, so throttle only affects rolling rear tyres
            maxSideAccel *= 1f - throttleGripLoss * Mathf.Max(0f, value);
        }

        Vector3 right = Vector3.ProjectOnPlane(point.right, hit.normal).normalized;
        float sideSpeed = Vector3.Dot(right, velocity);
        float sideAccel = Mathf.Clamp(-sideSpeed * grip, -maxSideAccel, maxSideAccel);
        _rb.AddForceAtPosition(right * sideAccel * massPerWheel, point.position);

        DrawDebugRay(hit.point, right * sideAccel, Color.red);
    }

    private void ApplySteering(Transform point, float value)
    {
        // Reduces the steering angle at higher speeds to prevent twitchy handling
        float speedFactor = Mathf.Lerp(1f, 0.4f, _rb.linearVelocity.magnitude / maxSpeed);
        Quaternion steer = Quaternion.Euler(0f, value * maxSteerAngle * speedFactor, 0f);
        point.localRotation = steer;

        DrawDebugRay(point.position, point.forward * 2f, Color.yellow);
    }

    void ApplySpring(Transform point, RaycastHit hit)
    {
        Vector3 origin = point.position;
        Vector3 springDir = point.up;

        float compression = restLength - hit.distance;
        float springVelocity = Vector3.Dot(springDir, _rb.GetPointVelocity(origin));
        float force = (stiffness * compression) - (damping * springVelocity);

        force = Mathf.Max(0f, force);

        _rb.AddForceAtPosition(springDir * force, origin);

        DrawDebugRay(origin, -springDir * hit.distance, Color.blue, 1);
    }
}