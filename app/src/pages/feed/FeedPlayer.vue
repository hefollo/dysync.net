<script lang="ts" setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';

// 与后端 DouyinFeedItemDto 保持一致
export interface FeedItem {
  id: string;
  title?: string;
  author?: string;
  authorId?: string;
  type?: string;
  typeName?: string;
  createTime?: string;
  resolution?: string;
  isImagePost?: boolean;
}

const props = defineProps<{
  items: FeedItem[];
  startIndex: number;
  total: number;
  hasMore: boolean;
}>();

const emit = defineEmits<{
  (e: 'close', index: number): void;
  (e: 'load-more'): void;
  (e: 'author', item: FeedItem): void;
}>();

const MUTED_KEY = 'dysync:feed:muted';
const AUTO_NEXT_KEY = 'dysync:feed:autoNext';
// 滚轮和触控板一次滑动会连续触发很多事件，切一条之后锁一小段时间
const WHEEL_LOCK = 650;
// 还剩几条没看时提前加载下一页
const LOAD_AHEAD = 4;

function readFlag(key: string, fallback: boolean) {
  try {
    const value = localStorage.getItem(key);
    return value === null ? fallback : value === '1';
  } catch {
    return fallback;
  }
}

function writeFlag(key: string, value: boolean) {
  try {
    localStorage.setItem(key, value ? '1' : '0');
  } catch {
    // 浏览器禁用本地存储时只是不记住偏好
  }
}

const scroller = ref<HTMLElement | null>(null);
const current = ref(Math.min(Math.max(props.startIndex, 0), Math.max(props.items.length - 1, 0)));
const paused = ref(false);
const muted = ref(readFlag(MUTED_KEY, false));
const autoNext = ref(readFlag(AUTO_NEXT_KEY, true));
// 浏览器不允许带声音自动播放时会被迫静音，这时提示用户点一下开声音
const forcedMute = ref(false);
const currentTime = ref(0);
const duration = ref(0);
const seeking = ref(false);
const titleExpanded = ref(false);
const failed = reactive<Record<string, boolean>>({});
const avatarFailed = reactive<Record<string, boolean>>({});

const videoEls = new Map<string, HTMLVideoElement>();
let scrollFrame = 0;
let wheelLockedUntil = 0;
let previousBodyOverflow = '';

const currentItem = computed(() => props.items[current.value]);
const progress = computed(() => (duration.value > 0 ? Math.min(currentTime.value / duration.value, 1) : 0));

const playUrl = (item: FeedItem) => `/api/video/play/${encodeURIComponent(item.id)}`;
const coverUrl = (item: FeedItem) => `/api/video/cover/${encodeURIComponent(item.id)}`;
const avatarUrl = (item: FeedItem) => `/api/video/avatar/${encodeURIComponent(item.id)}`;

// 只给当前这条和上下相邻的两条挂 video 元素，其余只占位，避免同时加载很多视频
const isNear = (index: number) => Math.abs(index - current.value) <= 1;

function setVideoRef(id: string, el: unknown) {
  if (el) {
    videoEls.set(id, el as HTMLVideoElement);
    if (currentItem.value?.id === id) syncPlayback();
  } else {
    videoEls.delete(id);
  }
}

function currentVideo() {
  const item = currentItem.value;
  return item ? videoEls.get(item.id) : undefined;
}

function syncPlayback() {
  const item = currentItem.value;
  videoEls.forEach((el, id) => {
    if (!item || id !== item.id) el.pause();
  });

  const el = currentVideo();
  if (!el || paused.value) return;

  el.muted = muted.value;
  const playing = el.play();
  if (playing) {
    playing.catch(() => {
      // 没有用户操作时带声音播放会被拒绝，退回静音播放
      if (el.muted) return;
      el.muted = true;
      muted.value = true;
      forcedMute.value = true;
      el.play().catch(() => {});
    });
  }
}

function setCurrent(index: number) {
  if (index === current.value || index < 0 || index >= props.items.length) return;

  const leaving = currentVideo();
  if (leaving) {
    leaving.pause();
    leaving.currentTime = 0;
  }

  current.value = index;
  paused.value = false;
  currentTime.value = 0;
  duration.value = 0;
  titleExpanded.value = false;
  nextTick(syncPlayback);

  if (props.hasMore && index >= props.items.length - LOAD_AHEAD) emit('load-more');
}

function goTo(index: number, smooth = true) {
  const el = scroller.value;
  if (!el) return;
  const target = Math.min(Math.max(index, 0), props.items.length - 1);
  el.scrollTo({ top: target * el.clientHeight, behavior: smooth ? 'smooth' : 'auto' });
}

function onScroll() {
  if (scrollFrame) return;
  scrollFrame = requestAnimationFrame(() => {
    scrollFrame = 0;
    const el = scroller.value;
    if (!el || !el.clientHeight) return;
    setCurrent(Math.round(el.scrollTop / el.clientHeight));
  });
}

function onWheel(e: WheelEvent) {
  if (Math.abs(e.deltaY) < 8 || Date.now() < wheelLockedUntil) return;
  wheelLockedUntil = Date.now() + WHEEL_LOCK;
  goTo(current.value + (e.deltaY > 0 ? 1 : -1));
}

function togglePlay() {
  const el = currentVideo();
  if (!el) return;
  if (el.paused) {
    paused.value = false;
    syncPlayback();
  } else {
    paused.value = true;
    el.pause();
  }
}

function toggleMute() {
  muted.value = !muted.value;
  forcedMute.value = false;
  writeFlag(MUTED_KEY, muted.value);
  const el = currentVideo();
  if (el) el.muted = muted.value;
}

function toggleAutoNext() {
  autoNext.value = !autoNext.value;
  writeFlag(AUTO_NEXT_KEY, autoNext.value);
}

function onTimeUpdate(item: FeedItem, e: Event) {
  if (item.id !== currentItem.value?.id || seeking.value) return;
  currentTime.value = (e.target as HTMLVideoElement).currentTime;
}

function onMetadata(item: FeedItem, e: Event) {
  if (item.id !== currentItem.value?.id) return;
  const value = (e.target as HTMLVideoElement).duration;
  duration.value = Number.isFinite(value) ? value : 0;
}

function onEnded(item: FeedItem) {
  if (item.id !== currentItem.value?.id) return;
  if (autoNext.value && current.value < props.items.length - 1) {
    goTo(current.value + 1);
    return;
  }

  // 单条循环，或者已经是最后一条：从头再放
  const el = currentVideo();
  if (el) {
    el.currentTime = 0;
    el.play().catch(() => {});
  }
}

function hideImage(e: Event) {
  (e.target as HTMLElement).style.display = 'none';
}

function seekFromPointer(e: PointerEvent) {
  const bar = e.currentTarget as HTMLElement;
  const rect = bar.getBoundingClientRect();
  if (!rect.width || !duration.value) return;
  const ratio = Math.min(Math.max((e.clientX - rect.left) / rect.width, 0), 1);
  currentTime.value = ratio * duration.value;
}

function onSeekStart(e: PointerEvent) {
  if (!duration.value) return;
  seeking.value = true;
  (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  seekFromPointer(e);
}

function onSeekMove(e: PointerEvent) {
  if (seeking.value) seekFromPointer(e);
}

function onSeekEnd() {
  if (!seeking.value) return;
  seeking.value = false;
  const el = currentVideo();
  if (el) el.currentTime = currentTime.value;
}

function seekBy(seconds: number) {
  const el = currentVideo();
  if (!el || !duration.value) return;
  el.currentTime = Math.min(Math.max(el.currentTime + seconds, 0), duration.value);
}

function onKeydown(e: KeyboardEvent) {
  const target = e.target as HTMLElement | null;
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;

  switch (e.key) {
    case 'ArrowDown':
    case 'PageDown':
      e.preventDefault();
      goTo(current.value + 1);
      break;
    case 'ArrowUp':
    case 'PageUp':
      e.preventDefault();
      goTo(current.value - 1);
      break;
    case 'ArrowRight':
      seekBy(5);
      break;
    case 'ArrowLeft':
      seekBy(-5);
      break;
    case ' ':
      e.preventDefault();
      togglePlay();
      break;
    case 'm':
    case 'M':
      toggleMute();
      break;
    case 'Escape':
      close();
      break;
  }
}

function onResize() {
  // 窗口大小变了以后每条的高度也变了，重新对齐到当前这条
  goTo(current.value, false);
}

function close() {
  emit('close', current.value);
}

function formatTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds < 0) return '00:00';
  const total = Math.floor(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

function formatDate(value?: string) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

// 列表被重置（换了筛选条件）后，当前位置可能超出范围
watch(
  () => props.items.length,
  (length) => {
    if (length > 0 && current.value >= length) {
      current.value = length - 1;
      nextTick(() => goTo(current.value, false));
    }
  }
);

onMounted(() => {
  previousBodyOverflow = document.body.style.overflow;
  document.body.style.overflow = 'hidden';
  window.addEventListener('keydown', onKeydown);
  window.addEventListener('resize', onResize);
  nextTick(() => {
    goTo(current.value, false);
    syncPlayback();
    if (props.hasMore && current.value >= props.items.length - LOAD_AHEAD) emit('load-more');
  });
});

onBeforeUnmount(() => {
  document.body.style.overflow = previousBodyOverflow;
  window.removeEventListener('keydown', onKeydown);
  window.removeEventListener('resize', onResize);
  if (scrollFrame) cancelAnimationFrame(scrollFrame);
  videoEls.forEach((el) => el.pause());
  videoEls.clear();
});
</script>

<template>
  <teleport to="body">
    <div class="feed-player" role="dialog" aria-modal="true" aria-label="视频播放">
      <div ref="scroller" class="feed-scroller" @scroll.passive="onScroll" @wheel.prevent="onWheel">
        <section v-for="(item, index) in props.items" :key="item.id" class="feed-slide">
          <template v-if="isNear(index)">
            <img class="feed-backdrop" :src="coverUrl(item)" alt="" referrerpolicy="no-referrer" @error="hideImage" />
            <video
              v-if="!failed[item.id]"
              :ref="(el) => setVideoRef(item.id, el)"
              class="feed-video"
              :src="playUrl(item)"
              :preload="index === current ? 'auto' : 'metadata'"
              playsinline
              webkit-playsinline
              @click="togglePlay"
              @timeupdate="onTimeUpdate(item, $event)"
              @loadedmetadata="onMetadata(item, $event)"
              @durationchange="onMetadata(item, $event)"
              @ended="onEnded(item)"
              @error="failed[item.id] = true"
            />
            <div v-else class="feed-error">
              <strong>这条视频播放不了</strong>
              <span>文件可能已被移动或删除，可以在「同步记录」里重新下载。</span>
            </div>
          </template>

          <template v-if="index === current">
            <div class="feed-shade"></div>
            <button v-if="paused && !failed[item.id]" type="button" class="feed-play-mark" aria-label="继续播放" @click="togglePlay">
              <svg viewBox="0 0 24 24" width="64" height="64" fill="currentColor"><path d="M8 5.14v13.72a1 1 0 0 0 1.53.85l10.9-6.86a1 1 0 0 0 0-1.7L9.53 4.29A1 1 0 0 0 8 5.14z" /></svg>
            </button>

            <div class="feed-info">
              <button type="button" class="feed-author" @click="emit('author', item)">@{{ item.author || '未知作者' }}</button>
              <p class="feed-title" :class="{ expanded: titleExpanded }" @click="titleExpanded = !titleExpanded">{{ item.title || '无标题' }}</p>
              <div class="feed-meta">
                <span v-if="formatDate(item.createTime)">{{ formatDate(item.createTime) }}</span>
                <span v-if="item.typeName">{{ item.typeName }}</span>
                <span v-if="item.isImagePost">图文</span>
                <span v-if="item.resolution">{{ item.resolution }}</span>
              </div>
            </div>

            <div class="feed-actions">
              <button type="button" class="feed-avatar" :title="`只看 ${item.author || '该作者'} 的视频`" @click="emit('author', item)">
                <img v-if="!avatarFailed[item.id]" :src="avatarUrl(item)" alt="" referrerpolicy="no-referrer" @error="avatarFailed[item.id] = true" />
                <span v-else>{{ (item.author || '作').slice(0, 1) }}</span>
              </button>

              <button type="button" class="feed-action" :class="{ off: muted }" @click="toggleMute">
                <svg v-if="!muted" viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M11 5 6 9H2v6h4l5 4V5z" /><path d="M15.5 8.5a5 5 0 0 1 0 7" /><path d="M18.5 5.5a9 9 0 0 1 0 13" />
                </svg>
                <svg v-else viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M11 5 6 9H2v6h4l5 4V5z" /><path d="m22 9-6 6" /><path d="m16 9 6 6" />
                </svg>
                <span>{{ muted ? '已静音' : '声音' }}</span>
              </button>

              <button type="button" class="feed-action" :class="{ off: !autoNext }" @click="toggleAutoNext">
                <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M17 2l4 4-4 4" /><path d="M3 11V9a4 4 0 0 1 4-4h14" /><path d="M7 22l-4-4 4-4" /><path d="M21 13v2a4 4 0 0 1-4 4H3" />
                </svg>
                <span>{{ autoNext ? '连播' : '单条循环' }}</span>
              </button>

              <button type="button" class="feed-action feed-nav" :disabled="current === 0" aria-label="上一条" @click="goTo(current - 1)">
                <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m6 15 6-6 6 6" /></svg>
              </button>
              <button type="button" class="feed-action feed-nav" :disabled="current >= props.items.length - 1" aria-label="下一条" @click="goTo(current + 1)">
                <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6" /></svg>
              </button>
            </div>
          </template>
        </section>

        <section v-if="props.items.length === 0" class="feed-slide feed-empty">没有可以播放的视频</section>
      </div>

      <header class="feed-top">
        <button type="button" class="feed-close" aria-label="返回" @click="close">
          <svg viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <button v-if="forcedMute" type="button" class="feed-unmute" @click="toggleMute">点这里开启声音</button>
        <span v-if="props.items.length" class="feed-counter">{{ current + 1 }} / {{ Math.max(props.total, props.items.length) }}</span>
      </header>

      <footer v-if="currentItem && !failed[currentItem.id]" class="feed-progress" :class="{ seeking }">
        <span class="feed-time">{{ formatTime(currentTime) }} / {{ formatTime(duration) }}</span>
        <div
          class="feed-bar"
          role="slider"
          aria-label="播放进度"
          :aria-valuenow="Math.round(progress * 100)"
          aria-valuemin="0"
          aria-valuemax="100"
          @pointerdown.prevent="onSeekStart"
          @pointermove="onSeekMove"
          @pointerup="onSeekEnd"
          @pointercancel="onSeekEnd"
        >
          <div class="feed-bar-track">
            <div class="feed-bar-fill" :style="{ width: progress * 100 + '%' }"></div>
          </div>
        </div>
      </footer>
    </div>
  </teleport>
</template>

<style scoped lang="less">
.feed-player {
  position: fixed;
  inset: 0;
  z-index: 3000;
  color: #ffffff;
  background: #000000;
  user-select: none;
  -webkit-tap-highlight-color: transparent;
}

.feed-scroller {
  height: 100%;
  overflow-y: scroll;
  overscroll-behavior: contain;
  scroll-snap-type: y mandatory;
  scrollbar-width: none;

  &::-webkit-scrollbar {
    display: none;
  }
}

.feed-slide {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  overflow: hidden;
  scroll-snap-align: start;
  scroll-snap-stop: always;
}

.feed-empty {
  color: rgba(255, 255, 255, 0.6);
  font-size: 15px;
}

// 用封面做一层模糊背景，横屏视频或宽屏窗口两侧不会是纯黑
.feed-backdrop {
  position: absolute;
  inset: -48px;
  width: calc(100% + 96px);
  height: calc(100% + 96px);
  object-fit: cover;
  filter: blur(36px) brightness(0.4);
  pointer-events: none;
}

.feed-video {
  position: relative;
  z-index: 1;
  width: 100%;
  height: 100%;
  object-fit: contain;
  cursor: pointer;
}

.feed-error {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 320px;
  padding: 0 24px;
  text-align: center;

  strong {
    font-size: 17px;
  }

  span {
    color: rgba(255, 255, 255, 0.65);
    font-size: 13px;
    line-height: 1.6;
  }
}

.feed-play-mark {
  position: absolute;
  top: 50%;
  left: 50%;
  z-index: 2;
  padding: 0;
  color: rgba(255, 255, 255, 0.78);
  border: 0;
  background: none;
  transform: translate(-50%, -50%);
  cursor: pointer;
  filter: drop-shadow(0 2px 12px rgba(0, 0, 0, 0.5));
}

// 底部的渐变压暗单独铺满整行，文字区域只占左边，避免在右侧按钮列留下一道接缝
.feed-shade {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  z-index: 2;
  height: 220px;
  background: linear-gradient(to top, rgba(0, 0, 0, 0.72), rgba(0, 0, 0, 0));
  pointer-events: none;
}

.feed-info {
  position: absolute;
  right: 84px;
  bottom: 0;
  left: 0;
  z-index: 2;
  padding: 0 16px calc(34px + env(safe-area-inset-bottom));
  text-align: left;
  pointer-events: none;

  > * {
    pointer-events: auto;
  }
}

.feed-author {
  max-width: 100%;
  padding: 0;
  overflow: hidden;
  color: #ffffff;
  border: 0;
  background: none;
  font-size: 17px;
  font-weight: 700;
  white-space: nowrap;
  text-overflow: ellipsis;
  cursor: pointer;
}

.feed-title {
  display: -webkit-box;
  max-width: 720px;
  margin: 8px 0 0;
  overflow: hidden;
  font-size: 15px;
  line-height: 1.5;
  word-break: break-word;
  cursor: pointer;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;

  &.expanded {
    max-height: 40vh;
    overflow-y: auto;
    -webkit-line-clamp: unset;
  }
}

.feed-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
  margin-top: 8px;
  color: rgba(255, 255, 255, 0.7);
  font-size: 12px;
}

.feed-actions {
  position: absolute;
  right: 10px;
  bottom: calc(44px + env(safe-area-inset-bottom));
  z-index: 3;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
  width: 64px;
}

.feed-avatar {
  display: grid;
  place-items: center;
  width: 50px;
  height: 50px;
  padding: 0;
  overflow: hidden;
  color: #ffffff;
  border: 2px solid #ffffff;
  border-radius: 50%;
  background: linear-gradient(135deg, #fe2c55, #7a5cff);
  font-size: 20px;
  font-weight: 700;
  cursor: pointer;

  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
}

.feed-action {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 3px;
  padding: 0;
  color: #ffffff;
  border: 0;
  background: none;
  font-size: 11px;
  cursor: pointer;
  filter: drop-shadow(0 1px 4px rgba(0, 0, 0, 0.6));

  &.off {
    color: rgba(255, 255, 255, 0.55);
  }

  &:disabled {
    opacity: 0.3;
    cursor: default;
  }
}

// 触屏设备靠手指上下滑，不需要上一条/下一条按钮
@media (hover: none) {
  .feed-nav {
    display: none;
  }
}

.feed-top {
  position: absolute;
  top: 0;
  right: 0;
  left: 0;
  z-index: 4;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: calc(10px + env(safe-area-inset-top)) 12px 28px;
  background: linear-gradient(to bottom, rgba(0, 0, 0, 0.5), rgba(0, 0, 0, 0));
  pointer-events: none;

  > * {
    pointer-events: auto;
  }
}

.feed-close {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  padding: 0;
  color: #ffffff;
  border: 0;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.28);
  cursor: pointer;
}

.feed-unmute {
  padding: 6px 14px;
  color: #ffffff;
  border: 0;
  border-radius: 999px;
  background: #fe2c55;
  font-size: 13px;
  cursor: pointer;
}

.feed-counter {
  margin-left: auto;
  padding: 4px 10px;
  border-radius: 999px;
  background: rgba(0, 0, 0, 0.28);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.feed-progress {
  position: absolute;
  right: 0;
  bottom: env(safe-area-inset-bottom);
  left: 0;
  z-index: 4;
  padding: 0 12px 6px;
}

.feed-time {
  display: block;
  margin-bottom: 2px;
  color: rgba(255, 255, 255, 0.75);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  text-align: left;
  opacity: 0;
  transition: opacity 0.15s;
}

.feed-progress:hover .feed-time,
.feed-progress.seeking .feed-time {
  opacity: 1;
}

// 进度条本身很细，外面包一层更高的区域方便点中
.feed-bar {
  display: flex;
  align-items: center;
  height: 18px;
  cursor: pointer;
  touch-action: none;
}

.feed-bar-track {
  width: 100%;
  height: 3px;
  overflow: hidden;
  border-radius: 2px;
  background: rgba(255, 255, 255, 0.25);
  transition: height 0.15s;
}

.feed-progress:hover .feed-bar-track,
.feed-progress.seeking .feed-bar-track {
  height: 6px;
}

.feed-bar-fill {
  height: 100%;
  border-radius: 2px;
  background: #ffffff;
}
</style>
