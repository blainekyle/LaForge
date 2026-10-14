package com.android.vending.licensing;

/**
 * Standard Google Play License Verification Library (LVL) callback interface.
 */
oneway interface ILicenseResultListener {
    void verifyLicense(int responseCode, String signedData, String signature);
}
