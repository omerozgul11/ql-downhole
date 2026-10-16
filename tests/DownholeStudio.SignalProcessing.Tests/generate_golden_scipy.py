# Regenerates golden-scipy.json with SciPy (independent reference). Usage: python -I generate_golden_scipy.py
import numpy as np, scipy.signal as ss, json
fs=1000.0
out={}
def mag(sos,freqs):
    w,h=ss.sosfreqz(sos,worN=np.array(freqs),fs=fs); return [float(abs(x)) for x in h]
freqs=[1,5,8,20,40,80,120,200,400]
out['lp4_100']=mag(ss.butter(4,100,'low',fs=fs,output='sos'),freqs)
out['hp4_10']=mag(ss.butter(4,10,'high',fs=fs,output='sos'),freqs)
out['bp4_8_80']=mag(ss.butter(4,[8,80],'band',fs=fs,output='sos'),freqs)
out['lp3_50']=mag(ss.butter(3,50,'low',fs=fs,output='sos'),freqs)
out['bp3_20_120']=mag(ss.butter(3,[20,120],'band',fs=fs,output='sos'),freqs)
b,a=ss.iirnotch(60,30,fs=fs); w,h=ss.freqz(b,a,worN=np.array(freqs+[60,59,61]),fs=fs); out['notch60']=[float(abs(x)) for x in h]
n=400; t=np.arange(n)/fs
x=np.sin(2*np.pi*12*t)+0.5*np.sin(2*np.pi*150*t)+0.2*np.cos(2*np.pi*3*t)+0.1*t
out['x']=x.tolist()
out['filtfilt_bp4_8_80']=ss.sosfiltfilt(ss.butter(4,[8,80],'band',fs=fs,output='sos'),x).tolist()
out['filtfilt_lp4_100']=ss.sosfiltfilt(ss.butter(4,100,'low',fs=fs,output='sos'),x).tolist()
out['sosfilt_hp4_10']=ss.sosfilt(ss.butter(4,10,'high',fs=fs,output='sos'),x).tolist()
out['detrend']=ss.detrend(x,type='linear').tolist()
out['freqs']=freqs
out['notch_freqs']=freqs+[60,59,61]
json.dump(out,open(__import__('os').path.join(__import__('os').path.dirname(__file__),'golden-scipy.json'),'w'))
print('ok', len(out))
