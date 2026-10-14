package com.laforge.lvlclienttest;

import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.Bundle;
import android.os.IBinder;
import android.os.RemoteException;
import android.util.Log;

import com.android.vending.licensing.ILicenseResultListener;
import com.android.vending.licensing.ILicensingService;

public class MainActivity extends Activity {
    private static final String TAG = "LVL_TEST";

    private boolean bound = false;
    private boolean finished = false;

    private final ILicenseResultListener.Stub listener = new ILicenseResultListener.Stub() {
        @Override
        public void verifyLicense(int responseCode, String signedData, String signature) {
            String msg;
            switch (responseCode) {
                case 0x0001:
                    msg = "LAFORGE_LVL_TEST: RECEIVED_NOT_LICENSED";
                    break;
                case 0x0100:
                case 0x0000:
                    msg = "LAFORGE_LVL_TEST: RECEIVED_LICENSED";
                    break;
                default:
                    msg = "LAFORGE_LVL_TEST: UNEXPECTED_CODE_" + responseCode;
                    break;
            }
            Log.i(TAG, msg);
            finishOnce();
        }
    };

    private final ServiceConnection connection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName name, IBinder service) {
            ILicensingService svc = ILicensingService.Stub.asInterface(service);
            try {
                svc.checkLicense(12345L, getPackageName(), listener);
            } catch (RemoteException e) {
                Log.e(TAG, "checkLicense failed", e);
            }
        }

        @Override
        public void onServiceDisconnected(ComponentName name) {
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent intent = new Intent("com.android.vending.licensing.ILicensingService");
        intent.setPackage("com.android.vending");
        boolean ok = false;
        try {
            ok = bindService(intent, connection, Context.BIND_AUTO_CREATE);
        } catch (SecurityException e) {
            Log.e(TAG, "bindService threw", e);
        }
        bound = ok;
        if (!ok) {
            Log.i(TAG, "LAFORGE_LVL_TEST: BIND_FAILED");
            finishOnce();
        }
    }

    private void finishOnce() {
        runOnUiThread(() -> {
            if (!finished) {
                finished = true;
                finish();
            }
        });
    }

    @Override
    protected void onDestroy() {
        if (bound) {
            try {
                unbindService(connection);
            } catch (IllegalArgumentException ignored) {
            }
            bound = false;
        }
        super.onDestroy();
    }
}
