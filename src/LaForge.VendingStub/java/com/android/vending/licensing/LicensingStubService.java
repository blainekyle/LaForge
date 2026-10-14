package com.android.vending.licensing;

import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import android.os.RemoteException;
import android.util.Log;

/**
 * Headless dummy implementation of Google Play's ILicensingService (package: com.android.vending).
 * Prevents apps that bind to com.android.vending.licensing.ILicensingService from crashing or hanging
 * on missing ServiceConnection bindings, while strictly respecting developer DRM by always returning
 * 0x0001 (NOT_LICENSED).
 */
public final class LicensingStubService extends Service {

    private static final String TAG = "LaForgeVendingStub";

    /**
     * Standard Google Play LVL response code for unlicensed state (0x0001).
     */
    public static final int RESPONSE_NOT_LICENSED = 0x0001;

    private final ILicensingService.Stub mBinder = new ILicensingService.Stub() {
        @Override
        public void checkLicense(long nonce, String packageName, ILicenseResultListener listener) throws RemoteException {
            Log.i(TAG, "LVL checkLicense IPC received: package=" + packageName
                    + ", nonce=" + nonce + " -> returning 0x0001 (NOT_LICENSED)");
            if (listener != null) {
                listener.verifyLicense(RESPONSE_NOT_LICENSED, null, null);
            }
        }
    };

    @Override
    public IBinder onBind(Intent intent) {
        Log.i(TAG, "Client bound to com.android.vending.licensing.ILicensingService: " + intent);
        return mBinder;
    }
}
