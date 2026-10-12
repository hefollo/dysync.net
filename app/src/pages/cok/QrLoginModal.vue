<script lang="ts" setup>
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { CheckCircleFilled, ReloadOutlined } from '@ant-design/icons-vue';
import { useApiStore } from '@/store';

export interface QrLoginResult {
  cookie: string;
  secUserId?: string;
  nickname?: string;
}

const props = defineProps<{ visible: boolean }>();
const emit = defineEmits<{
  (e: 'update:visible', value: boolean): void;
  (e: 'success', result: QrLoginResult): void;
}>();

// 与后端 DouyinQrLoginStates 保持一致
type QrState = 'starting' | 'waiting' | 'scanned' | 'confirming' | 'success' | 'expired' | 'failed' | 'canceled';

// 与后端 DouyinQrLoginActionDto 保持一致
interface BrowserAction {
  type: 'down' | 'move' | 'up' | 'scroll' | 'text' | 'key';
  x?: number;
  y?: number;
  deltaY?: number;
  text?: string;
  key?: 'Enter' | 'Backspace' | 'Tab' | 'Escape';
}

const POLL_INTERVAL = 1500;
// 浏览器画面的刷新间隔：刚操作过时刷得快一些，方便看到点击、拖动的反馈
const SCREEN_INTERVAL_IDLE = 1200;
const SCREEN_INTERVAL_ACTIVE = 150;
const ACTIVE_WINDOW = 4000;
const MOVE_THROTTLE = 40;
const WHEEL_THROTTLE = 80;

const sessionId = ref('');
const state = ref<QrState>('starting');
const statusText = ref('');
const qrImage = ref('');
const qrVersion = ref(0);
const screenImage = ref('');
const showScreen = ref(false);
const inputText = ref('');
const screenImg = ref<HTMLImageElement | null>(null);

let pollTimer: ReturnType<typeof setTimeout> | undefined;
let screenTimer: ReturnType<typeof setTimeout> | undefined;
let screenLoading = false;
let lastInteractAt = 0;
let lastMoveAt = 0;
let lastWheelAt = 0;
let pressing = false;
// 后端提示需要在页面上操作时自动切到浏览器画面，只切一次，之后尊重用户自己的选择
let autoSwitched = false;
// 操作必须按顺序送达（按下、移动、松开），用一条 Promise 链串起来
let actionChain: Promise<unknown> = Promise.resolve();
// 每次重新获取二维码都换一个序号，旧会话还没返回的请求回来后直接丢弃
let runSeq = 0;

const isFinal = computed(() => ['success', 'expired', 'failed', 'canceled'].includes(state.value));
const isScanning = computed(() => state.value === 'waiting' || state.value === 'scanned');
// 二维码图片没取到时，直接展示浏览器画面让用户扫
const screenVisible = computed(() => isScanning.value && (showScreen.value || !qrImage.value));

function clearTimers() {
  if (pollTimer) {
    clearTimeout(pollTimer);
    pollTimer = undefined;
  }
  if (screenTimer) {
    clearTimeout(screenTimer);
    screenTimer = undefined;
  }
}

function cancelSession() {
  const id = sessionId.value;
  sessionId.value = '';
  if (id) {
    useApiStore()
      .QrLoginCancel(id)
      .catch(() => {});
  }
}

function stop() {
  runSeq++;
  clearTimers();
  cancelSession();
}

function start() {
  stop();
  const seq = runSeq;
  state.value = 'starting';
  statusText.value = '正在启动浏览器...';
  qrImage.value = '';
  qrVersion.value = 0;
  screenImage.value = '';
  showScreen.value = false;
  inputText.value = '';
  autoSwitched = false;
  pressing = false;
  lastInteractAt = 0;
  actionChain = Promise.resolve();

  useApiStore()
    .QrLoginStart()
    .then((res) => {
      if (seq !== runSeq) {
        // 等待期间弹窗已关闭或已重新获取，这个会话不要了
        useApiStore()
          .QrLoginCancel(res.data.id)
          .catch(() => {});
        return;
      }
      sessionId.value = res.data.id;
      applyStatus(res.data);
      schedulePoll(seq);
    })
    .catch((err) => {
      if (seq !== runSeq) return;
      state.value = 'failed';
      statusText.value = err?.message || '无法开始扫码，请稍后重试';
    });
}

function schedulePoll(seq: number) {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = setTimeout(() => poll(seq), POLL_INTERVAL);
}

function applyStatus(data: any) {
  state.value = data.state;
  statusText.value = data.message || '';
  if (data.qrImage) {
    qrImage.value = data.qrImage;
  }
  qrVersion.value = data.qrVersion || 0;
  if (data.showBrowser && !autoSwitched) {
    autoSwitched = true;
    showScreen.value = true;
  }
}

function poll(seq: number) {
  if (seq !== runSeq || !sessionId.value) return;

  useApiStore()
    .QrLoginStatus(sessionId.value, qrVersion.value)
    .then((res) => {
      if (seq !== runSeq) return;
      applyStatus(res.data);

      if (res.data.state === 'success') {
        const result: QrLoginResult = {
          cookie: res.data.cookie,
          secUserId: res.data.secUserId || undefined,
          nickname: res.data.nickname || undefined,
        };
        // 结果已拿到，通知后端丢弃会话里的 Cookie
        stop();
        emit('success', result);
        emit('update:visible', false);
        return;
      }

      if (isFinal.value) return;
      schedulePoll(seq);
    })
    .catch((err) => {
      if (seq !== runSeq) return;
      state.value = 'failed';
      statusText.value = err?.message || '扫码状态查询失败，请重试';
    });
}

// 浏览器画面：显示期间循环刷新，上一张回来后再请求下一张
function loadScreen(seq: number) {
  if (seq !== runSeq || !sessionId.value || !screenVisible.value || screenLoading) return;

  screenLoading = true;
  useApiStore()
    .QrLoginScreenshot(sessionId.value)
    .then((res) => {
      if (seq === runSeq && res.data?.image) {
        screenImage.value = res.data.image;
      }
    })
    .catch(() => {})
    .then(() => {
      screenLoading = false;
      if (seq !== runSeq || !screenVisible.value) return;
      const delay = Date.now() - lastInteractAt < ACTIVE_WINDOW ? SCREEN_INTERVAL_ACTIVE : SCREEN_INTERVAL_IDLE;
      if (screenTimer) clearTimeout(screenTimer);
      screenTimer = setTimeout(() => loadScreen(seq), delay);
    });
}

function sendAction(action: BrowserAction) {
  const id = sessionId.value;
  if (!id) return;
  lastInteractAt = Date.now();
  actionChain = actionChain.then(() =>
    useApiStore()
      .QrLoginAction(id, action)
      .catch(() => {})
  );
}

// 把鼠标在图片上的位置换算成页面坐标（截图像素与页面坐标一致）
function toPagePoint(e: MouseEvent) {
  const img = screenImg.value;
  if (!img || !img.naturalWidth) return null;
  const rect = img.getBoundingClientRect();
  if (!rect.width || !rect.height) return null;
  return {
    x: Math.round(((e.clientX - rect.left) / rect.width) * img.naturalWidth),
    y: Math.round(((e.clientY - rect.top) / rect.height) * img.naturalHeight),
  };
}

function onPointerDown(e: PointerEvent) {
  if (e.button !== 0) return;
  const point = toPagePoint(e);
  if (!point) return;
  pressing = true;
  (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  sendAction({ type: 'down', ...point });
}

function onPointerMove(e: PointerEvent) {
  if (!pressing || Date.now() - lastMoveAt < MOVE_THROTTLE) return;
  const point = toPagePoint(e);
  if (!point) return;
  lastMoveAt = Date.now();
  sendAction({ type: 'move', ...point });
}

function onPointerUp(e: PointerEvent) {
  if (!pressing) return;
  pressing = false;
  const point = toPagePoint(e);
  if (point) sendAction({ type: 'up', ...point });
}

function onWheel(e: WheelEvent) {
  if (Date.now() - lastWheelAt < WHEEL_THROTTLE) return;
  const point = toPagePoint(e);
  if (!point) return;
  lastWheelAt = Date.now();
  sendAction({ type: 'scroll', ...point, deltaY: e.deltaY });
}

function sendText() {
  if (!inputText.value) return;
  sendAction({ type: 'text', text: inputText.value });
  inputText.value = '';
}

function sendKey(key: 'Enter' | 'Backspace') {
  sendAction({ type: 'key', key });
}

function toggleScreen() {
  showScreen.value = !showScreen.value;
}

function close() {
  emit('update:visible', false);
}

watch(screenVisible, (visible) => {
  if (visible) {
    loadScreen(runSeq);
  } else if (screenTimer) {
    clearTimeout(screenTimer);
    screenTimer = undefined;
  }
});

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      start();
    } else {
      stop();
    }
  },
  { immediate: true }
);

onBeforeUnmount(stop);
</script>

<template>
  <a-modal
    :visible="props.visible"
    title="抖音扫码登录"
    :footer="null"
    :width="screenVisible ? 1000 : 420"
    :z-index="1050"
    :mask-closable="false"
    destroy-on-close
    @cancel="close"
  >
    <div class="qr-login">
      <template v-if="state === 'failed' || state === 'canceled'">
        <a-alert type="error" show-icon :message="statusText || '扫码登录失败'" class="qr-login-alert" />
        <p class="qr-login-hint">也可以关闭此窗口，按文档手动复制 Cookie 粘贴。</p>
        <a-button type="primary" @click="start">
          <template #icon><ReloadOutlined /></template>
          重试
        </a-button>
      </template>

      <template v-else>
        <template v-if="screenVisible">
          <div class="qr-login-screen">
            <img
              v-if="screenImage"
              ref="screenImg"
              :src="screenImage"
              alt="浏览器画面"
              draggable="false"
              @pointerdown.prevent="onPointerDown"
              @pointermove="onPointerMove"
              @pointerup="onPointerUp"
              @pointercancel="onPointerUp"
              @wheel.prevent="onWheel"
              @contextmenu.prevent
            />
            <a-spin v-else tip="正在获取浏览器画面..." />
          </div>

          <div class="qr-login-input">
            <a-input
              v-model:value="inputText"
              placeholder="先点画面里的输入框，再在这里输入（如短信验证码）"
              allow-clear
              @press-enter="sendText"
            />
            <a-button type="primary" :disabled="!inputText" @click="sendText">发送</a-button>
            <a-button @click="sendKey('Backspace')">退格</a-button>
            <a-button @click="sendKey('Enter')">回车</a-button>
          </div>
        </template>

        <div v-else class="qr-login-code">
          <img v-if="qrImage" :src="qrImage" alt="抖音登录二维码" :class="{ 'is-dimmed': state !== 'waiting' }" />
          <a-spin v-else />

          <div v-if="state === 'scanned' || state === 'confirming'" class="qr-login-mask">
            <CheckCircleFilled class="qr-login-mask-icon" />
          </div>
          <div v-else-if="state === 'expired'" class="qr-login-mask">
            <a-button type="primary" @click="start">
              <template #icon><ReloadOutlined /></template>
              刷新二维码
            </a-button>
          </div>
        </div>

        <p class="qr-login-status" :class="'is-' + state">{{ statusText }}</p>

        <template v-if="isScanning">
          <p v-if="screenVisible" class="qr-login-hint">
            这是服务器上浏览器的实时画面，可以直接点击、拖动、滚动来完成抖音的身份验证。画面有一点延迟，点完稍等一下再操作。
          </p>
          <p v-else class="qr-login-hint">
            打开抖音 App，点击左上角「扫一扫」。手机和本服务不在同一网络时，抖音可能要求额外的身份验证。
          </p>
          <a-button v-if="qrImage" type="link" size="small" @click="toggleScreen">
            {{ showScreen ? '返回二维码' : '需要验证或没反应？打开浏览器画面操作' }}
          </a-button>
        </template>
      </template>
    </div>
  </a-modal>
</template>

<style scoped lang="less">
.qr-login {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 8px 0 4px;
  text-align: center;
}

.qr-login-code {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 240px;
  height: 240px;
  padding: 10px;
  border: 1px solid #e8edf3;
  border-radius: 12px;
  // 二维码需要白底才能被识别，暗色主题下也保持白色
  background: #ffffff;
  box-sizing: border-box;

  img {
    width: 100%;
    height: 100%;
    image-rendering: pixelated;

    &.is-dimmed {
      opacity: 0.12;
    }
  }
}

.qr-login-mask {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}

.qr-login-mask-icon {
  color: #22a45a;
  font-size: 56px;
}

.qr-login-screen {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  min-height: 240px;
  overflow: hidden;
  border: 1px solid #e8edf3;
  border-radius: 12px;
  background: #ffffff;

  img {
    display: block;
    width: 100%;
    cursor: pointer;
    user-select: none;
    // 拖动滑块时不能触发页面滚动或图片拖拽
    touch-action: none;
    -webkit-user-drag: none;
  }
}

.qr-login-input {
  display: flex;
  gap: 8px;
  width: 100%;

  :deep(.ant-input-affix-wrapper),
  :deep(.ant-input) {
    flex: 1;
  }
}

.qr-login-status {
  margin: 0;
  font-size: 14px;
  font-weight: 600;

  &.is-scanned,
  &.is-confirming {
    color: #22a45a;
  }

  &.is-expired {
    color: #faad14;
  }
}

.qr-login-hint {
  margin: 0;
  color: #8c96a1;
  font-size: 12px;
  line-height: 1.6;
}

.qr-login-alert {
  width: 100%;
  text-align: left;
}
</style>
