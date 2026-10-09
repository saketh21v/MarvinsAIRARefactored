/*
Automobilista 2 shared memory mapping structures.

Transcribed for MarvinsAIRA Refactored from the version-14 SharedMemory.h that ships with Automobilista 2
(Support/SharedMemory/AMS2_SharedMemoryExampleApp) and is mirrored at
https://github.com/viper4gh/CREST2-AMS2/blob/master/SharedMemory.h (SHARED_MEMORY_VERSION 14).

The game publishes one "$pcars2$" memory mapped file (Options > System > Shared Memory = Project CARS 2)
containing a single SharedMemory struct. The header sets no pack pragma, and no field is wider than 4 bytes,
so the MSVC default alignment matches pack(4). Field names are kept exactly as in the header so the layout
can be cross-checked line by line. Do NOT reorder, rename, or resize any fields - byte layout fidelity with
the game is required.

C# bool is not a reliable stand-in for a C++ bool under sequential layout, so every bool is a byte plus the
explicit pad bytes the pack-4 layout inserts. Arrays are [InlineArray] value types (see InlineArrays.cs)
so the struct stays blittable and can be read with MemoryMarshal.Read with no heap allocation.

Ams2Layout.Check() compares the compiled offsets with the map measured from this header. The bridge refuses
to start when that check fails.
*/

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarvinsAIRARefactored.GameBridges.Ams2;

public static class Ams2Constants
{
	public const string SharedMemoryMapName = "$pcars2$";

	public const int SharedMemoryVersion = 14;

	public const int StringLengthMax = 64;
	public const int StoredParticipantsMax = 64;
	public const int TyreCount = 4;
	public const int TyreCompoundNameLengthMax = 40;

	// measured from SharedMemory.h version 14; Ams2Layout.Check confirms the compiled struct agrees
	public const int SequenceNumberOffset = 7320;
	public const int SharedMemorySize = 20700;

	public const int TyreFrontLeft = 0;
	public const int TyreFrontRight = 1;
	public const int TyreRearLeft = 2;
	public const int TyreRearRight = 3;

	// local vector slots. The header never names the axes; motion-rig readers of this map treat them as
	// +X = right, +Y = up, +Z = forward. That reading is assumed here and is not capture-validated.
	public const int VecX = 0;
	public const int VecY = 1;
	public const int VecZ = 2;
}

public enum Ams2GameState : uint
{
	Exited = 0,
	FrontEnd = 1,
	IngamePlaying = 2,
	IngamePaused = 3,
	IngameInmenuTimeTicking = 4,
	IngameRestarting = 5,
	IngameReplay = 6,
	FrontEndReplay = 7
}

public enum Ams2SessionState : uint
{
	Invalid = 0,
	Practice = 1,
	Test = 2,
	Qualify = 3,
	FormationLap = 4,
	Race = 5,
	TimeAttack = 6
}

public enum Ams2RaceState : uint
{
	Invalid = 0,
	NotStarted = 1,
	Racing = 2,
	Finished = 3,
	Disqualified = 4,
	Retired = 5,
	Dnf = 6
}

public enum Ams2FlagColour : uint
{
	None = 0,
	Green = 1,
	Blue = 2,
	WhiteSlowCar = 3,
	WhiteFinalLap = 4,
	Red = 5,
	Yellow = 6,
	DoubleYellow = 7,
	BlackAndWhite = 8,
	BlackOrangeCircle = 9,
	Black = 10,
	Chequered = 11
}

public enum Ams2PitMode : uint
{
	None = 0,
	DrivingIntoPits = 1,
	InPit = 2,
	DrivingOutOfPits = 3,
	InGarage = 4,
	DrivingOutOfGarage = 5
}

[Flags]
public enum Ams2CarFlags : uint
{
	Headlight = 1 << 0,
	EngineActive = 1 << 1,
	EngineWarning = 1 << 2,
	SpeedLimiter = 1 << 3,
	Abs = 1 << 4,
	Handbrake = 1 << 5,
	Tcs = 1 << 6,
	Scs = 1 << 7
}

public enum Ams2Terrain : uint
{
	Road = 0,
	LowGripRoad = 1,
	BumpyRoad1 = 2,
	BumpyRoad2 = 3,
	BumpyRoad3 = 4,
	Marbles = 5,
	GrassyBerms = 6,
	Grass = 7,
	Gravel = 8,
	BumpyGravel = 9,
	RumbleStrips = 10,
	Drains = 11,
	TyreWalls = 12,
	CementWalls = 13,
	Guardrails = 14,
	Sand = 15,
	BumpySand = 16,
	Dirt = 17,
	BumpyDirt = 18,
	DirtRoad = 19,
	BumpyDirtRoad = 20,
	Pavement = 21,
	DirtBank = 22,
	Wood = 23,
	DryVerge = 24,
	ExitRumbleStrips = 25,
	Grasscrete = 26,
	LongGrass = 27,
	SlopeGrass = 28,
	Cobbles = 29,
	SandRoad = 30,
	BakedClay = 31,
	Astroturf = 32,
	SnowHalf = 33,
	SnowFull = 34,
	DamagedRoad1 = 35,
	TrainTrackRoad = 36,
	BumpyCobbles = 37,
	AriesOnly = 38,
	OrionOnly = 39,
	B1Rumbles = 40,
	B2Rumbles = 41,
	RoughSandMedium = 42,
	RoughSandHeavy = 43,
	SnowWalls = 44,
	IceRoad = 45,
	RunoffRoad = 46,
	IllegalStrip = 47,
	PaintConcrete = 48,
	PaintConcreteIllegal = 49,
	RallyTarmac = 50
}

public enum Ams2YellowFlagState
{
	Invalid = -1,
	None = 0,
	Pending = 1,
	PitsClosed = 2,
	PitLeadLap = 3,
	PitsOpen = 4,
	PitsOpen2 = 5,
	LastLap = 6,
	Resume = 7,
	RaceHalt = 8
}

[InlineArray( Ams2Constants.StoredParticipantsMax )]
public struct Ams2ParticipantArray
{
	private Ams2ParticipantInfo _element0;
}

[InlineArray( Ams2Constants.StoredParticipantsMax )]
public struct Ams2Float64
{
	private float _element0;
}

[InlineArray( Ams2Constants.StoredParticipantsMax )]
public struct Ams2UInt64
{
	private uint _element0;
}

[InlineArray( Ams2Constants.TyreCount )]
public struct Ams2UInt4
{
	private uint _element0;
}

[InlineArray( Ams2Constants.TyreCompoundNameLengthMax )]
public struct Ams2Byte40
{
	private byte _element0;
}

[InlineArray( Ams2Constants.TyreCount )]
public struct Ams2TyreCompoundNames
{
	private Ams2Byte40 _element0;
}

[InlineArray( Ams2Constants.StoredParticipantsMax )]
public struct Ams2NameArray64
{
	private ByteArray64 _element0;
}

[InlineArray( Ams2Constants.StoredParticipantsMax )]
public struct Ams2Vec3Array64
{
	private FloatArray3 _element0;
}

// ParticipantInfo. 100 bytes: bool, char[64], 3 bytes of pack-4 padding, then five 4-byte fields and a vec3.
[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct Ams2ParticipantInfo
{
	public byte mIsActive;
	public ByteArray64 mName;
	private byte _pad0;
	private byte _pad1;
	private byte _pad2;
	public FloatArray3 mWorldPosition;
	public float mCurrentLapDistance;
	public uint mRacePosition;
	public uint mLapsCompleted;
	public uint mCurrentLap;
	public int mCurrentSector;
}

// SharedMemory, version 14. Pack 1 with the header's padding written out, so the offsets do not depend on
// how the runtime aligns a C# bool.
[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public struct Ams2Shared
{
	public uint mVersion;
	public uint mBuildVersionNumber;

	public uint mGameState;
	public uint mSessionState;
	public uint mRaceState;

	public int mViewedParticipantIndex;
	public int mNumParticipants;
	public Ams2ParticipantArray mParticipantInfo;

	public float mUnfilteredThrottle;
	public float mUnfilteredBrake;
	public float mUnfilteredSteering;
	public float mUnfilteredClutch;

	public ByteArray64 mCarName;
	public ByteArray64 mCarClassName;

	public uint mLapsInEvent;
	public ByteArray64 mTrackLocation;
	public ByteArray64 mTrackVariation;
	public float mTrackLength;

	public int mNumSectors;
	public byte mLapInvalidated;
	private byte _padLapInvalidated0;
	private byte _padLapInvalidated1;
	private byte _padLapInvalidated2;
	public float mBestLapTime;
	public float mLastLapTime;
	public float mCurrentTime;
	public float mSplitTimeAhead;
	public float mSplitTimeBehind;
	public float mSplitTime;
	public float mEventTimeRemaining;
	public float mPersonalFastestLapTime;
	public float mWorldFastestLapTime;
	public float mCurrentSector1Time;
	public float mCurrentSector2Time;
	public float mCurrentSector3Time;
	public float mFastestSector1Time;
	public float mFastestSector2Time;
	public float mFastestSector3Time;
	public float mPersonalFastestSector1Time;
	public float mPersonalFastestSector2Time;
	public float mPersonalFastestSector3Time;
	public float mWorldFastestSector1Time;
	public float mWorldFastestSector2Time;
	public float mWorldFastestSector3Time;

	public uint mHighestFlagColour;
	public uint mHighestFlagReason;

	public uint mPitMode;
	public uint mPitSchedule;

	public uint mCarFlags;
	public float mOilTempCelsius;
	public float mOilPressureKPa;
	public float mWaterTempCelsius;
	public float mWaterPressureKPa;
	public float mFuelPressureKPa;
	public float mFuelLevel;
	public float mFuelCapacity;
	public float mSpeed;
	public float mRpm;
	public float mMaxRPM;
	public float mBrake;
	public float mThrottle;
	public float mClutch;
	public float mSteering;
	public int mGear;
	public int mNumGears;
	public float mOdometerKM;
	public byte mAntiLockActive;
	private byte _padAntiLock0;
	private byte _padAntiLock1;
	private byte _padAntiLock2;
	public int mLastOpponentCollisionIndex;
	public float mLastOpponentCollisionMagnitude;
	public byte mBoostActive;
	private byte _padBoost0;
	private byte _padBoost1;
	private byte _padBoost2;
	public float mBoostAmount;

	public FloatArray3 mOrientation;
	public FloatArray3 mLocalVelocity;
	public FloatArray3 mWorldVelocity;
	public FloatArray3 mAngularVelocity;
	public FloatArray3 mLocalAcceleration;
	public FloatArray3 mWorldAcceleration;
	public FloatArray3 mExtentsCentre;

	public Ams2UInt4 mTyreFlags;
	public Ams2UInt4 mTerrain;
	public FloatArray4 mTyreY;
	public FloatArray4 mTyreRPS;
	public FloatArray4 mTyreSlipSpeed;
	public FloatArray4 mTyreTemp;
	public FloatArray4 mTyreGrip;
	public FloatArray4 mTyreHeightAboveGround;
	public FloatArray4 mTyreLateralStiffness;
	public FloatArray4 mTyreWear;
	public FloatArray4 mBrakeDamage;
	public FloatArray4 mSuspensionDamage;
	public FloatArray4 mBrakeTempCelsius;
	public FloatArray4 mTyreTreadTemp;
	public FloatArray4 mTyreLayerTemp;
	public FloatArray4 mTyreCarcassTemp;
	public FloatArray4 mTyreRimTemp;
	public FloatArray4 mTyreInternalAirTemp;

	public uint mCrashState;
	public float mAeroDamage;
	public float mEngineDamage;

	public float mAmbientTemperature;
	public float mTrackTemperature;
	public float mRainDensity;
	public float mWindSpeed;
	public float mWindDirectionX;
	public float mWindDirectionY;
	public float mCloudBrightness;

	public uint mSequenceNumber;

	public FloatArray4 mWheelLocalPositionY;
	public FloatArray4 mSuspensionTravel;
	public FloatArray4 mSuspensionVelocity;
	public FloatArray4 mAirPressure;
	public float mEngineSpeed;
	public float mEngineTorque;
	public FloatArray2 mWings;
	public float mHandBrake;

	public Ams2Float64 mCurrentSector1Times;
	public Ams2Float64 mCurrentSector2Times;
	public Ams2Float64 mCurrentSector3Times;
	public Ams2Float64 mFastestSector1Times;
	public Ams2Float64 mFastestSector2Times;
	public Ams2Float64 mFastestSector3Times;
	public Ams2Float64 mFastestLapTimes;
	public Ams2Float64 mLastLapTimes;
	public ByteArray64 mLapsInvalidated;
	public Ams2UInt64 mRaceStates;
	public Ams2UInt64 mPitModes;
	public Ams2Vec3Array64 mOrientations;
	public Ams2Float64 mSpeeds;
	public Ams2NameArray64 mCarNames;
	public Ams2NameArray64 mCarClassNames;

	public int mEnforcedPitStopLap;
	public ByteArray64 mTranslatedTrackLocation;
	public ByteArray64 mTranslatedTrackVariation;
	public float mBrakeBias;
	public float mTurboBoostPressure;
	public Ams2TyreCompoundNames mTyreCompound;
	public Ams2UInt64 mPitSchedules;
	public Ams2UInt64 mHighestFlagColours;
	public Ams2UInt64 mHighestFlagReasons;
	public Ams2UInt64 mNationalities;
	public float mSnowDensity;

	public float mSessionDuration;
	public int mSessionAdditionalLaps;

	public FloatArray4 mTyreTempLeft;
	public FloatArray4 mTyreTempCenter;
	public FloatArray4 mTyreTempRight;

	public uint mDrsState;

	public FloatArray4 mRideHeight;

	public uint mJoyPad0;
	public uint mDPad;

	public int mAntiLockSetting;
	public int mTractionControlSetting;

	public int mErsDeploymentMode;
	public byte mErsAutoModeEnabled;
	private byte _padErs0;
	private byte _padErs1;
	private byte _padErs2;

	public float mClutchTemp;
	public float mClutchWear;
	public byte mClutchOverheated;
	public byte mClutchSlipping;
	private byte _padClutch0;
	private byte _padClutch1;

	public int mYellowFlagState;

	public byte mSessionIsPrivate;
	private byte _padPrivate0;
	private byte _padPrivate1;
	private byte _padPrivate2;
	public int mLaunchStage;
}

public static class Ams2Layout
{
	// null when the compiled struct matches the version-14 map; otherwise a description of the first mismatches
	public static string? Check()
	{
		var sample = default( Ams2Shared );

		var mismatches = new List<string>();

		void Expect( string name, int actual, int expected )
		{
			if ( actual != expected )
			{
				mismatches.Add( $"{name} is {actual}, expected {expected}" );
			}
		}

		Expect( "Ams2ParticipantInfo", Unsafe.SizeOf<Ams2ParticipantInfo>(), 100 );
		Expect( "Ams2Shared", Unsafe.SizeOf<Ams2Shared>(), Ams2Constants.SharedMemorySize );

		Expect( "mParticipantInfo", Offset( ref sample, ref Unsafe.As<Ams2ParticipantInfo, byte>( ref sample.mParticipantInfo[ 0 ] ) ), 28 );
		Expect( "mName", Offset( ref sample, ref sample.mParticipantInfo[ 0 ].mName[ 0 ] ), 29 );
		Expect( "mWorldPosition", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mParticipantInfo[ 0 ].mWorldPosition[ 0 ] ) ), 96 );
		Expect( "mUnfilteredThrottle", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mUnfilteredThrottle ) ), 6428 );
		Expect( "mLapInvalidated", Offset( ref sample, ref sample.mLapInvalidated ), 6712 );
		Expect( "mBestLapTime", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mBestLapTime ) ), 6716 );
		Expect( "mSpeed", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mSpeed ) ), 6848 );
		Expect( "mSteering", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mSteering ) ), 6872 );
		Expect( "mAntiLockActive", Offset( ref sample, ref sample.mAntiLockActive ), 6888 );
		Expect( "mOrientation", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mOrientation[ 0 ] ) ), 6908 );
		Expect( "mSequenceNumber", Offset( ref sample, ref Unsafe.As<uint, byte>( ref sample.mSequenceNumber ) ), Ams2Constants.SequenceNumberOffset );
		Expect( "mSuspensionVelocity", Offset( ref sample, ref Unsafe.As<float, byte>( ref sample.mSuspensionVelocity[ 0 ] ) ), 7356 );
		Expect( "mLaunchStage", Offset( ref sample, ref Unsafe.As<int, byte>( ref sample.mLaunchStage ) ), 20696 );

		if ( mismatches.Count == 0 )
		{
			return null;
		}

		return string.Join( "; ", mismatches );
	}

	private static int Offset( ref Ams2Shared shared, ref byte field )
	{
		return (int) Unsafe.ByteOffset( ref Unsafe.As<Ams2Shared, byte>( ref shared ), ref field );
	}
}
