package com.android.vending.licensing;

import com.android.vending.licensing.ILicenseResultListener;

/**
 * Standard Google Play License Verification Library (LVL) service interface.
 */
oneway interface ILicensingService {
    void checkLicense(long nonce, String packageName, in ILicenseResultListener listener);
}
