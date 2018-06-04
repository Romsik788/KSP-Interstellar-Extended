﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FNPlugin.Extensions;

namespace FNPlugin.Beamedpower
{
    class ModuleSolarSail : PartModule, IBeamedPowerReceiver
    {
        // Persistent Variables
        [KSPField(isPersistant = true)]
        public bool IsEnabled = false;
        [KSPField(isPersistant = true)]
        public double previousPeA;
        [KSPField(isPersistant = true)]
        public double previousAeA;
        [KSPField(isPersistant = true)]
        public float previousFixedDeltaTime;

        // Persistent False
        [KSPField]
        public double reflectedPhotonRatio = 1f;
        [KSPField(guiActiveEditor = true, guiName = "Surface Area", guiUnits = " m\xB2")]
        public double surfaceArea = 144400;
        [KSPField(guiActiveEditor = true, guiName = "Diameter", guiUnits = " m")]
        public double diameter;
        [KSPField(guiActiveEditor = true, guiName = "Mass", guiUnits = " t", guiFormat = "F3")]
        public double partMass;

        [KSPField]
        public float effectSize1 = 2.5f;
        [KSPField]
        public float effectSize2 = 5;

        [KSPField]
        public string animName;
        [KSPField]
        public float initialAnimationSpeed = 1;
        [KSPField]
        public float initialAnimationNormalizedTime = 0.5f;
        [KSPField]
        public float initialAnimationTargetWeight = 0.01f;

        // GUI
        // Force and Acceleration
        [KSPField(guiActive = true, guiName = "Solar Flux", guiFormat = "F3", guiUnits = " W/m\xB2")]
        public double averageSolarFluxInWatt;
        [KSPField(guiActive = true, guiName = "Solar Force")]
        public string solarForceBasedOnFlux;
        [KSPField(guiActive = true, guiName = "Sail Force")]
        public string forceAcquired = "";
        [KSPField(guiActive = true, guiName = "Acceleration")]
        public string solarAcc = "";

        [KSPField(guiActive = false, guiName = "Sail Cos", guiFormat = "F6")]
        public double cosConeAngle;
        [KSPField(guiActive = true, guiName = "Sun Incedense", guiFormat = "F4", guiUnits = "°")]
        public double sailAngle;

        [KSPField(guiActive = true, guiName = "Abs Periapsis Change", guiFormat = "F4")]
        public double periapsisChange;
        [KSPField(guiActive = true, guiName = "Abs Apapsis Change", guiFormat = "F4")]
        public double apapsisChange;
        [KSPField(guiActive = true, guiName = "Orbit size Change", guiFormat = "F4")]
        public double orbitSizeChange;

        protected Transform surfaceTransform = null;
        protected Animation solarSailAnim = null;

        // Reference distance for calculating thrust: sun to Kerbin (m)
        const double kerbin_distance = 13599840256;
        // average solar radiance at earth in W/m2
        const double solarConstant = 1360.8;
        // Solar Presure  P = W / c =  9.08e-6;
        const double thrust_coeff = 2 * solarConstant / GameConstants.speedOfLight;

        const double radToDegreeMult = 180d / Math.PI;

        // Display numbers for force and acceleration
        double solar_force_d = 0;
        double solar_acc_d = 0;
        double solarForceBasedOnFlux_d = 0;

        CelestialBody _localStar;
        IDictionary<VesselMicrowavePersistence, KeyValuePair<MicrowaveRoute, IList<VesselRelayPersistence>>> _transmitData;

        Queue<double> periapsisChangeQueue = new Queue<double>(20);
        Queue<double> apapsisChangeQueue = new Queue<double>(20);
        Queue<double> solarFluxQueue = new Queue<double>(100);

        GameObject solar_effect;
        Renderer solar_effect_renderer;
        Collider solar_effect_collider;

        public int ReceiverType { get { return 7; } }                       // receiver from either top or bottom

        public double Diameter { get { return diameter; } }

        public double ApertureMultiplier { get { return 1; } }

        public double MinimumWavelength { get { return 0.000000620; } }     // receive optimally from red visible light

        public double MaximumWavelength { get { return 0.001; } }           // receive up to maximum infrared

        public double HighSpeedAtmosphereFactor { get { return 1; } }

        public double FacingThreshold { get { return 0; } }

        public double FacingSurfaceExponent { get { return 1; } }

        public double FacingEfficiencyExponent { get { return 1; } }

        public double SpotsizeNormalizationExponent { get { return 1; } }

        public bool CanBeActiveInAtmosphere { get { return false; } }

        public Vessel Vessel { get { return vessel; } }

        public Part Part { get { return part; } }

        // GUI to deploy sail
        [KSPEvent(guiActiveEditor = true,  guiActive = true, guiName = "Deploy Sail", active = true, guiActiveUncommand = true, guiActiveUnfocused = true)]
        public void DeploySail()
        {
            if (animName == null || solarSailAnim == null)
                return;

            solarSailAnim[animName].speed = 0.5f;
            solarSailAnim[animName].normalizedTime = 0f;
            solarSailAnim.Blend(animName, 0.1f);

            IsEnabled = true;
        }

        // GUI to retract sail
        [KSPEvent(guiActiveEditor = true, guiActive = true, guiName = "Retract Sail", active = false, guiActiveUncommand = true, guiActiveUnfocused = true)]
        public void RetractSail()
        {
            if (animName == null || solarSailAnim == null)
                return;

            solarSailAnim[animName].speed = -0.5f;
            solarSailAnim[animName].normalizedTime = 1f;
            solarSailAnim.Blend(animName, 0.1f);

            IsEnabled = false;
        }

        // Initialization
        public override void OnStart(PartModule.StartState state)
        {
            diameter = Math.Sqrt(surfaceArea);
            
            if (state == StartState.None || state == StartState.Editor)
                return;

            if (animName != null)
                solarSailAnim = part.FindModelAnimators(animName).FirstOrDefault();

            // start with deployed sail  when enabled
            if (IsEnabled)
            {
                solarSailAnim[animName].speed = initialAnimationSpeed;
                solarSailAnim[animName].normalizedTime = initialAnimationNormalizedTime;
                solarSailAnim.Blend(animName, initialAnimationTargetWeight);
            }

            this.part.force_activate();

            InitializeBeam();
        }

        private void InitializeBeam()
        {
            var zero = Vector3.zero;

            solar_effect = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            solar_effect_renderer = solar_effect.GetComponent<Renderer>();
            solar_effect_collider = solar_effect.GetComponent<Collider>();
            solar_effect_collider.enabled = false;

            solar_effect.transform.localScale = new Vector3(0, 0, 0);
            solar_effect.transform.position = new Vector3(zero.x, zero.y + zero.y, zero.z);
            solar_effect.transform.rotation = part.transform.rotation;
            solar_effect_renderer.material.shader = Shader.Find("Unlit/Transparent");
            solar_effect_renderer.material.color = new Color(Color.yellow.r, Color.yellow.g, Color.yellow.b, 0.5f);

            solar_effect_renderer.material.mainTexture = GameDatabase.Instance.GetTexture("WarpPlugin/ParticleFX/warp2", false);
            solar_effect_renderer.receiveShadows = false;
            solar_effect_renderer.material.renderQueue = 1001;
        }

        public void Update()
        {
            // Sail deployment GUI
            Events["DeploySail"].active = !IsEnabled;
            Events["RetractSail"].active = IsEnabled;

            if (HighLogic.LoadedSceneIsFlight)
                return;

            diameter = Math.Sqrt(surfaceArea);
            partMass = part.mass;
        }

        /// <summary>
        /// Is called by KSP while the part is active
        /// </summary>
        public override void OnUpdate()
        {   // update local star
            _localStar = PluginHelper.GetCurrentStar();
            // update available beamed power transmitters
            _transmitData = BeamedPowerHelper.GetConnectedTransmitters(this);

            // Text fields (acc & force)
            Fields["solarAcc"].guiActive = IsEnabled;
            Fields["forceAcquired"].guiActive = IsEnabled;

            forceAcquired = solar_force_d.ToString("E") + " N";
            solarAcc = solar_acc_d.ToString("E") + " m/s";
            solarForceBasedOnFlux = solarForceBasedOnFlux_d.ToString("E") + " N/m\xB2";
        }

        public override void OnFixedUpdate()
        {
            UpdateSolarFlux();

            solar_force_d = 0;
            solar_acc_d = 0;
            cosConeAngle = 0;

            if (FlightGlobals.fetch == null || _localStar == null || part == null || vessel == null)
                return;

            TimeWarp.GThreshold = 2;

            UpdateChangeGui();

            var universalTime = Planetarium.GetUniversalTime();

            Vector3d positionSun = _localStar.getPositionAtUT(universalTime);
            Vector3d positionVessel = vessel.orbit.getPositionAtUT(universalTime);

            // calculate vector between vessel and star
            Vector3d ownsunPosition = positionVessel - positionSun;

            // take part vector 
            Vector3d partNormal = this.part.transform.up;

            // Magnitude of force proportional to cosine-squared of angle between sun-line and normal
            cosConeAngle = Vector3d.Dot(ownsunPosition.normalized, partNormal);

            // convert to angle in degree
            sailAngle = Math.Acos(cosConeAngle) * radToDegreeMult;
            // add negative
            if (Vector3d.Dot(this.part.transform.right, ownsunPosition) < 0)
                sailAngle = -sailAngle;

            // If normal points away from sun, negate so our force is always away from the sun
            // so that turning the backside towards the sun thrusts correctly
            if (cosConeAngle < 0)
            {
                // recalculate Magnitude of force proportional to cosine-squared of angle between sun-line and normal
                partNormal = -partNormal;
                cosConeAngle = Vector3d.Dot(ownsunPosition.normalized, partNormal);
            }

            // calculate solar light force at current location
            solarForceBasedOnFlux_d = averageSolarFluxInWatt / GameConstants.speedOfLight;

            // Force from sunlight
            Vector3d solarForce = CalculateSolarForce(this, partNormal, cosConeAngle, solarForceBasedOnFlux_d);

            UpdateBeams(solarForce, ownsunPosition);

            if (!IsEnabled)
                return;

            // Calculate acceleration from sunlight
            Vector3d solarAccel = solarForce / vessel.totalMass / 1000d;

            // Acceleration from sunlight per second
            Vector3d fixedSolatAccel = solarAccel * TimeWarp.fixedDeltaTime;

            // Apply acceleration
            if (this.vessel.packed)
                vessel.orbit.Perturb(fixedSolatAccel, universalTime);
            else
                vessel.ChangeWorldVelocity(fixedSolatAccel);

            // Update displayed force & acceleration
            solar_force_d = solarForce.magnitude;
            solar_acc_d = solarAccel.magnitude;
        }

        private void UpdateBeams(Vector3d force3d, Vector3d ownsunPosition)
        {
            var endBeamPos = part.transform.position + ownsunPosition.normalized * 10000;
            var midPos = part.transform.position - endBeamPos;
            var timeCorrection = TimeWarp.CurrentRate > 1 ? -vessel.obt_velocity * TimeWarp.fixedDeltaTime : Vector3d.zero;

            var solarVectorX = ownsunPosition.normalized.x * 90;
            var solarVectorY = ownsunPosition.normalized.y * 90 - 90;
            var solarVectorZ = ownsunPosition.normalized.z * 90;

            solar_effect.transform.localRotation = new Quaternion((float)solarVectorX, (float)solarVectorY, (float)solarVectorZ, 0);
            solar_effect.transform.localScale = new Vector3(effectSize1, 10000, effectSize1);
            solar_effect.transform.position = new Vector3((float)(part.transform.position.x + midPos.x + timeCorrection.x), (float)(part.transform.position.y + midPos.y + timeCorrection.y), (float)(part.transform.position.z + midPos.z + timeCorrection.z));
        }

        private void UpdateSolarFlux()
        {
            if (vessel.solarFlux > 0)
            {
                solarFluxQueue.Enqueue(vessel.solarFlux);
                if (solarFluxQueue.Count > 100)
                    solarFluxQueue.Dequeue();
                averageSolarFluxInWatt = solarFluxQueue.Average();
            }
            else
            {
                if (solarFluxQueue.Count > 0)
                    solarFluxQueue.Dequeue();
                averageSolarFluxInWatt = 0;
            }
        }

        private void UpdateChangeGui()
        {
            var averageFixedDeltaTime = (previousFixedDeltaTime + TimeWarp.fixedDeltaTime) / 2f;

            periapsisChangeQueue.Enqueue((vessel.orbit.PeA - previousPeA) / averageFixedDeltaTime);
            if (periapsisChangeQueue.Count > 20)
                periapsisChangeQueue.Dequeue();
            periapsisChange = periapsisChangeQueue.Count > 10 
                ?  periapsisChangeQueue.OrderBy(m => m).Skip(5).Take(10).Average() 
                : periapsisChangeQueue.Average();

            apapsisChangeQueue.Enqueue((vessel.orbit.ApA - previousAeA) / averageFixedDeltaTime);
            if (apapsisChangeQueue.Count > 20)
                apapsisChangeQueue.Dequeue();
            apapsisChange = apapsisChangeQueue.Count > 10
                ? apapsisChangeQueue.OrderBy(m => m).Skip(5).Take(10).Average()
                : apapsisChangeQueue.Average();

            orbitSizeChange = periapsisChange + apapsisChange;

            previousPeA = vessel.orbit.PeA;
            previousAeA = vessel.orbit.ApA;
            previousFixedDeltaTime = TimeWarp.fixedDeltaTime;
        }

        // Calculate solar force as function of
        // sail, orbit, transform, and UT
        public static Vector3d CalculateSolarForce(ModuleSolarSail sail, Vector3d partNormal, double cosConeAngle, double solarForceAtDistance)
        {
            return partNormal * cosConeAngle * cosConeAngle * sail.surfaceArea * sail.reflectedPhotonRatio * solarForceAtDistance;
        }
    }
}