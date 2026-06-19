## OMA-URI Setting 1


- **Name:** Provide a name for the OMA-URI setting to distinguish it from other similar settings.
- **Description:** *(Optional)* Provide a description for the OMA-URI setting to further differentiate settings.
- **OMA-URI:** `./Device/Vendor/MSFT/SharedPC/EnableSharedPCModeWithOneDriveSync`
- **Data Type:** Boolean
- **Value:** `True`

## First Setting

### Name
`DisableOneDriveFileSync`

### Description
This setting is used to ensure that on a Shared PC, where drives are automatically hidden, OneDrive sync is still enabled.

### OMA-URI
`./Device/Vendor/MSFT/Policy/Config/System/DisableOneDriveFileSync`

### Data Type
Integer

### Value
`0`

## Second Setting

### Name
`MDMWinsOverGP`

### Description
Gives priority to Intune MDM over GPO

### OMA-URI
`./Device/Vendor/MSFT/Policy/Config/ControlPolicyConflict/MDMWinsOverGP`

### Data Type
Integer

### Value
`1`
