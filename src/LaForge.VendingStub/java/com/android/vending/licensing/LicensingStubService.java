package com.android.vending.licensing;

import android.app.Service;
import android.content.Intent;
import android.os.Binder;
import android.os.Bundle;
import android.os.IBinder;
import android.os.Parcel;
import android.os.RemoteException;
import android.util.Log;
import java.util.ArrayList;

/**
 * LaForge Headless Google Play Licensing (LVL) & Billing Stub Service.
 *
 * Implements com.android.vending.licensing.ILicensingService and always returns
 * 0x0001 (NOT_LICENSED) to prevent NullPointerException crashes in apps with hard
 * Play Store IPC dependencies while strictly respecting developer DRM.
 */
public final class LicensingStubService extends Service {
    private static final String TAG = "LaForgeVendingStub";

    /**
     * Standard Google Play LVL response code:
     * 0x0000 = LICENSED
     * 0x0001 = NOT_LICENSED
     * 0x0002 = LICENSED_OLD_KEY
     */
    public static final int RESPONSE_NOT_LICENSED = 0x0001;

    private final ILicensingService.Stub mBinder = new ILicensingService.Stub() {
        @Override
        public void checkLicense(long nonce, String packageName, ILicenseResultListener listener) {
            Log.i(TAG, "ILicensingService.checkLicense intercepted: package=" + packageName
                    + " nonce=" + nonce + " -> returning 0x0001 (NOT_LICENSED)");
            if (listener != null) {
                try {
                    listener.verifyLicense(RESPONSE_NOT_LICENSED, null, null);
                } catch (RemoteException e) {
                    Log.w(TAG, "Failed to deliver NOT_LICENSED callback to " + packageName, e);
                }
            }
        }
    };

    private final Binder mBillingStub = new Binder() {
        @Override
        protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
            int callingUid = Binder.getCallingUid();
            String[] pkgs = getPackageManager().getPackagesForUid(callingUid);
            String callerPkg = (pkgs != null && pkgs.length > 0) ? pkgs[0] : ("uid:" + callingUid);
            Log.i(TAG, "ILicensingService / InAppBillingService request intercepted from package="
                    + callerPkg + " txCode=" + code + " -> returning 0x0001 (NOT_LICENSED)");

            if (code == INTERFACE_TRANSACTION) {
                if (reply != null) {
                    reply.writeString("com.android.vending.billing.IInAppBillingService");
                }
                return true;
            }

            if (reply != null) {
                reply.writeNoException();
                // Code 1 = isBillingSupported -> return 3 (BILLING_UNAVAILABLE / NOT_LICENSED)
                if (code == 1 || code == 5) {
                    reply.writeInt(RESPONSE_NOT_LICENSED);
                } else {
                    // Return Bundle with RESPONSE_CODE = 0 and empty owned item lists
                    Bundle b = new Bundle();
                    b.putInt("RESPONSE_CODE", 0);
                    b.putStringArrayList("INAPP_PURCHASE_ITEM_LIST", new ArrayList<String>());
                    b.putStringArrayList("INAPP_PURCHASE_DATA_LIST", new ArrayList<String>());
                    b.putStringArrayList("INAPP_DATA_SIGNATURE_LIST", new ArrayList<String>());
                    reply.writeInt(1);
                    b.writeToParcel(reply, 0);
                }
            }
            return true;
        }
    };

    @Override
    public IBinder onBind(Intent intent) {
        String action = (intent != null && intent.getAction() != null) ? intent.getAction() : "";
        Log.i(TAG, "Service bound with action=" + action + " -> active LVL stub (0x0001 NOT_LICENSED)");
        if ("com.android.vending.billing.InAppBillingService.BIND".equals(action)) {
            try {
                mBinder.checkLicense(System.currentTimeMillis(), "com.clearvisions.explorer", new ILicenseResultListener.Stub() {
                    @Override
                    public void verifyLicense(int responseCode, String signedData, String signature) {
                        Log.i(TAG, "Verified LVL responseCode=0x000" + responseCode + " (NOT_LICENSED) for InAppBilling bind");
                    }
                });
            } catch (RemoteException ignored) {
            }
            return mBillingStub;
        }
        return mBinder;
    }
}
