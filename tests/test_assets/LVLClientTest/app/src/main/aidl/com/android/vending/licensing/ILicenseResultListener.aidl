package com.android.vending.licensing;

oneway interface ILicenseResultListener {
    void verifyLicense(int responseCode, String signedData, String signature);
}
